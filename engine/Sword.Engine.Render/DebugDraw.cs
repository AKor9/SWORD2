using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ui;

namespace Sword.Engine.Render;

/// <summary>Вершина отрезка отладочного рисования (<c>DebugVertex</c> в common/debug.hlsl).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct DebugVertex
{
    public Vector3 Position;
    public uint Colour;
}

/// <summary>Капсула отладочного рисования (<c>DebugCapsule</c> в common/debug.hlsl): ось — локальная Y поворота.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct DebugCapsule
{
    public Vector3 Center;
    public float Radius;
    public Quaternion Rotation;
    public float HalfLength;
    public uint Colour;
    public uint Pad0, Pad1;
}

/// <summary>
/// Отладочное рисование (раздел 6 плана, фаза Debug — после сцены): отрезки, капсулы и текст в мире для диагностики фич.
/// Кадр копит примитивы (<see cref="Line"/>, <see cref="Capsule"/>, <see cref="Text"/>) прямо в кольца кадра, проход
/// (<see cref="AddPass"/>) рисует всё накопленное поверх цели — заднего буфера или слоя снимка
/// (<see cref="RenderFrame.CaptureLayer"/>); проходов в кадре может быть несколько, накопленное — одно: проход рисует накопленное
/// к его регистрации, с начала кадра или с отметки (<see cref="Mark"/>: слой снимка одного оверлея — 6б). Глубины сцены нет:
/// примитивы видны сквозь геометрию («рентген» поверх персонажей). Капсулы — полупрозрачные, с плотной кромкой (видны и
/// одна поверх другой), без вершинного буфера: сетка — из SV_VertexID; отрезки — поверх капсул. Текст — проекцией точки
/// мира на экран, UI-проходом (<see cref="UiPass"/>, шрифты пака). Цвет — <c>0xAABBGGRR</c>, как у интерфейса.
/// Установившийся кадр памяти не выделяет. Переполнение — лишнее не рисуется, предупреждение в журнал один раз.
/// </summary>
public sealed class DebugDraw : IDisposable
{
    /// <summary>
    /// Отметка накопленного (<see cref="Mark"/>): проход с ней (<see cref="AddPass"/>, <c>since</c>) рисует только добавленное после
    /// неё — у каждого оверлея свой слой снимка, хотя кадр копит всё в одних кольцах. По умолчанию — начало кадра.
    /// </summary>
    public readonly record struct DebugMark(int LineVertices, int Capsules, int Texts);

    /// <summary>Вершин отрезков (по две на отрезок), капсул и подписей на кадр.</summary>
    public const uint MaxLineVertices = 1 << 17, MaxCapsules = 4096;

    public const int MaxTexts = 512;

    /// <summary>Проходов отладочного рисования в кадре не больше: работа прохода — заранее созданные делегаты.</summary>
    public const int MaxPassesPerFrame = 4;

    /// <summary>Сетка капсулы: вершин по кругу и колец на полусферу; вершин на капсулу — <see cref="CapsuleVertices"/>.</summary>
    public const int CapsuleAround = 16, CapsuleRings = 4;

    public const uint CapsuleVertices = (2 * CapsuleRings + 1) * CapsuleAround * 6;

    /// <summary>
    /// Слоты корневых констант прохода: матрица вида-проекции — 16 подряд, глаз — 3 подряд, кольца кадра, первая вершина отрезков
    /// и первая капсула прохода (с отметки).
    /// </summary>
    private const int SlotViewProj = 0, SlotEye = 16, SlotLines = 19, SlotCapsules = 20, SlotLineBase = 21, SlotCapsuleBase = 22;

    private const string Shader = "common/debug.hlsl";
    private static readonly GraphicsPipelineDesc Capsules = new(Shader, "VsCapsule", "PsCapsule", BlendMode.Alpha);
    private static readonly GraphicsPipelineDesc Lines = new(Shader, "VsLine", "PsLine", BlendMode.Alpha, Lines: true);

    private readonly Renderer _renderer;
    private readonly FrameData<DebugVertex> _lines;
    private readonly FrameData<DebugCapsule> _capsules;
    private readonly (Vector3 World, string Text, uint Colour, float Size)[] _texts = new (Vector3, string, uint, float)[MaxTexts];
    private int _lineVertices, _capsuleCount, _textCount, _passes;
    private bool _overflowReported;
    private UiDrawList? _textList;
    private readonly Work[] _work = new Work[MaxPassesPerFrame];
    private readonly Action<RenderPassContext>[] _record = new Action<RenderPassContext>[MaxPassesPerFrame];

    /// <summary>Работа одного прохода кадра: камера и какие примитивы рисовать — с отметки до накопленного к его регистрации.</summary>
    private sealed class Work
    {
        public Matrix4x4 ViewProj;
        public Vector3 Eye;
        public int LineStart, LineVertices, CapsuleStart, Capsules;
    }

    internal DebugDraw(Renderer renderer)
    {
        _renderer = renderer;
        _lines = renderer.CreateFrameData<DebugVertex>("Debug.Lines", MaxLineVertices);
        _capsules = renderer.CreateFrameData<DebugCapsule>("Debug.Capsules", MaxCapsules);
        for (int i = 0; i < MaxPassesPerFrame; i++)
        {
            var work = _work[i] = new Work();
            _record[i] = ctx => Record(ctx, work);
        }
    }

    /// <summary>Числа отладочного рисования для шейдеров.</summary>
    public static void Contribute(ShaderContract c)
    {
        c.Section("отладочное рисование (Sword.Engine.Render.DebugDraw)");
        c.Add("DEBUG_AROUND", CapsuleAround, "вершин капсулы по кругу");
        c.Add("DEBUG_RINGS", CapsuleRings, "колец капсулы на полусферу");
        c.AddSlot("DEBUG_EYE_X", SlotEye, "глаз камеры: x (y, z — следующие слоты), float битами");
        c.AddSlot("DEBUG_EYE_Y", SlotEye + 1);
        c.AddSlot("DEBUG_EYE_Z", SlotEye + 2);
        c.AddSlot("DEBUG_LINES", SlotLines, "вершины отрезков (кольцо кадра)");
        c.AddSlot("DEBUG_CAPSULES", SlotCapsules, "капсулы (кольцо кадра)");
        c.AddSlot("DEBUG_LINE_BASE", SlotLineBase, "первая вершина отрезков прохода (отметка)");
        c.AddSlot("DEBUG_CAPSULE_BASE", SlotCapsuleBase, "первая капсула прохода (отметка)");
        c.AddSize<DebugVertex>("DEBUG_VERTEX_SIZE");
        c.AddSize<DebugCapsule>("DEBUG_CAPSULE_SIZE");
    }

    /// <summary>Накоплено в этом кадре: отрезков, капсул, подписей.</summary>
    public int LineCount => _lineVertices / 2;
    public int CapsuleCount => _capsuleCount;
    public int TextCount => _textCount;

    /// <summary>В этом кадре нечего рисовать.</summary>
    public bool IsEmpty => _lineVertices == 0 && _capsuleCount == 0 && _textCount == 0;

    /// <summary>Отметка накопленного сейчас: проход с ней рисует только то, что добавят после.</summary>
    public DebugMark Mark() => new(_lineVertices, _capsuleCount, _textCount);

    /// <summary>Отрезок мира <paramref name="a"/>–<paramref name="b"/>.</summary>
    public void Line(Vector3 a, Vector3 b, uint colour)
    {
        if (_lineVertices + 2 > MaxLineVertices)
        {
            Overflow();
            return;
        }
        var slot = _lines.Slot;
        slot[_lineVertices++] = new DebugVertex { Position = a, Colour = colour };
        slot[_lineVertices++] = new DebugVertex { Position = b, Colour = colour };
    }

    /// <summary>Капсула: центр, поворот (ось — локальная Y), радиус и полудлина оси, м.</summary>
    public void Capsule(Vector3 center, Quaternion rotation, float radius, float halfLength, uint colour)
    {
        if (_capsuleCount >= MaxCapsules)
        {
            Overflow();
            return;
        }
        _capsules.Slot[_capsuleCount++] = new DebugCapsule
        {
            Center = center, Radius = radius, Rotation = rotation, HalfLength = halfLength, Colour = colour,
        };
    }

    /// <summary>
    /// Подпись в точке мира: центр строки — в проекции точки, высота букв <paramref name="size"/> пикселей. Строка
    /// хранится до прохода: без выделений — только готовые строки.
    /// </summary>
    public void Text(Vector3 world, string text, uint colour, float size = 14)
    {
        if (_textCount >= MaxTexts)
        {
            Overflow();
            return;
        }
        _texts[_textCount++] = (world, text, colour, size);
    }

    private void Overflow()
    {
        if (_overflowReported) return;
        _overflowReported = true;
        Log.Warning("debug", $"отладочному рисованию не хватило места: отрезков {LineCount}/{MaxLineVertices / 2}, капсул " +
            $"{_capsuleCount}/{MaxCapsules}, подписей {_textCount}/{MaxTexts} — лишнее не рисуется");
    }

    /// <summary>
    /// Проход «накопленное поверх <paramref name="target"/>» (цель размера кадра) камерой <paramref name="viewProj"/>
    /// (вектор слева, как <c>Matrix4x4</c>) с глазом <paramref name="eye"/>; подписи — UI-проходом следом за ним. Рисуется
    /// накопленное к этому вызову с отметки <paramref name="since"/> (по умолчанию — с начала кадра).
    /// <paramref name="clear"/> — очистить цель этим цветом (слой снимка: прозрачный); тогда проход есть, даже если рисовать
    /// нечего. Без очистки и без накопленного — прохода нет.
    /// </summary>
    public void AddPass(RenderFrame frame, RenderTexture target, in Matrix4x4 viewProj, Vector3 eye, string name = "debug", Vector4? clear = null,
        DebugMark since = default)
    {
        int lineStart = Math.Clamp(since.LineVertices, 0, _lineVertices), capsuleStart = Math.Clamp(since.Capsules, 0, _capsuleCount);
        int textStart = Math.Clamp(since.Texts, 0, _textCount);
        bool shapes = _lineVertices > lineStart || _capsuleCount > capsuleStart;
        if (!shapes && _textCount == textStart && clear is null) return;
        if (_passes == MaxPassesPerFrame) throw new InvalidOperationException($"проходов отладочного рисования в кадре больше {MaxPassesPerFrame}");
        if (shapes || clear is not null)
        {
            var work = _work[_passes];
            work.ViewProj = viewProj;
            work.Eye = eye;
            work.LineStart = lineStart;
            work.LineVertices = _lineVertices;
            work.CapsuleStart = capsuleStart;
            work.Capsules = _capsuleCount;
            frame.Graph.AddPass(name).Color(target, clear).Execute(_record[_passes]);
            _passes++;
        }
        if (_textCount > textStart) AddText(frame, target, viewProj, name + ".text", textStart);
    }

    /// <summary>Подписи с <paramref name="start"/>: точки мира — в пиксели кадра (за камерой — не рисуются), строки — UI-проходом поверх цели.</summary>
    private void AddText(RenderFrame frame, RenderTexture target, in Matrix4x4 viewProj, string name, int start)
    {
        var ui = _renderer.Ui;
        _textList ??= ui.CreateDrawList();
        _textList.Clear();
        int w = frame.Width, h = frame.Height;
        for (int i = start; i < _textCount; i++)
        {
            var (world, text, colour, size) = _texts[i];
            var c = Vector4.Transform(new Vector4(world, 1), viewProj);
            if (c.W <= 1e-4f) continue;
            float x = (c.X / c.W * 0.5f + 0.5f) * w, y = (0.5f - c.Y / c.W * 0.5f) * h;
            float half = _textList.Measure(UiFace.Body, size, text) * 0.5f + size * 0.3f;
            _textList.Rect(x - half, y - size * 0.7f, x + half, y + size * 0.7f, 0xA0000000, size * 0.3f);   // подложка
            _textList.TextCentered(UiFace.Body, size, new Vector2(x, y - size * 0.5f), colour, text);
        }
        ui.Add(frame, _textList, name, target);
    }

    /// <summary>Начало кадра (<see cref="Renderer.Render"/>): накопленное прошлым кадром забыто, проходов нет.</summary>
    internal void BeginFrame()
    {
        _lineVertices = _capsuleCount = _passes = 0;
        Array.Clear(_texts, 0, _textCount);   // строки прошлого кадра не держим
        _textCount = 0;
    }

    private void Record(RenderPassContext ctx, Work work)
    {
        var m = work.ViewProj;
        ReadOnlySpan<float> vp = [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
        for (int i = 0; i < 16; i++) ctx.SetConstant(SlotViewProj + i, vp[i]);
        ctx.SetConstant(SlotEye, work.Eye.X);
        ctx.SetConstant(SlotEye + 1, work.Eye.Y);
        ctx.SetConstant(SlotEye + 2, work.Eye.Z);
        ctx.SetConstant(SlotLines, _lines.Srv);
        ctx.SetConstant(SlotCapsules, _capsules.Srv);
        ctx.SetConstant(SlotLineBase, work.LineStart);
        ctx.SetConstant(SlotCapsuleBase, work.CapsuleStart);
        if (work.Capsules > work.CapsuleStart)
        {
            ctx.SetPipeline(Capsules);
            ctx.Draw(CapsuleVertices, (uint)(work.Capsules - work.CapsuleStart));
        }
        if (work.LineVertices > work.LineStart)
        {
            ctx.SetPipeline(Lines);
            ctx.Draw((uint)(work.LineVertices - work.LineStart));
        }
    }

    public void Dispose()
    {
        _lines.Dispose();
        _capsules.Dispose();
    }
}
