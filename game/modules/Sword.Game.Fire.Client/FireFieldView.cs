using System.Numerics;
using Sword.Engine.Render;

namespace Sword.Game.Fire;

/// <summary>
/// Вид поля огня (отладка, фаза Debug): площадка сима — плоскость к камере в показанном центре огня, плотность ρ выхода поля — тепловой
/// шкалой (чёрный → красный → жёлтый → белый), полупрозрачно; пустая площадка — тёмный квадрат. Рисуется поверх кадра без глубины
/// сцены (виден и сквозь персонажа) — на экран или в слой снимка. Кадр — 0 Б: делегат прохода один, аргументы — массив по порядку.
/// </summary>
internal sealed class FireFieldView
{
    public const string ShaderFile = "fire/fire_field.hlsl";

    private const int SlotCx = 16, SlotCy = 17, SlotCz = 18, SlotUx = 19, SlotUy = 20, SlotUz = 21, SlotVx = 22, SlotVy = 23, SlotVz = 24,
        SlotField = 25, SlotOpacity = 26;

    private static readonly GraphicsPipelineDesc Pipeline = new(ShaderFile, "VsFireField", "PsFireField", BlendMode.Alpha);

    /// <summary>Непрозрачность плотности 1 (пустой площадки — 0.15 от неё).</summary>
    public const float Opacity = 0.8f;

    private struct Args
    {
        public Matrix4x4 ViewProj;
        public Vector3 Center, U, V;
        public int Field;
    }

    private Args[] _args = new Args[4];
    private int _count, _next;
    private ulong _frame = ulong.MaxValue;
    private readonly Action<RenderPassContext> _record;

    public FireFieldView() => _record = Record;

    /// <summary>Слоты шейдера вида поля в контракт.</summary>
    public static void Contribute(ShaderContract c) => c
        .Section("Fire: field view (Sword.Game.Fire.Client)")
        .AddSlot("gFireCx", SlotCx).AddSlot("gFireCy", SlotCy).AddSlot("gFireCz", SlotCz)
        .AddSlot("gFireUx", SlotUx).AddSlot("gFireUy", SlotUy).AddSlot("gFireUz", SlotUz)
        .AddSlot("gFireVx", SlotVx).AddSlot("gFireVy", SlotVy).AddSlot("gFireVz", SlotVz)
        .AddSlot("gFireField", SlotField).AddSlot("gFireOpacity", SlotOpacity);

    /// <summary>
    /// Проход вида поля в цель <paramref name="target"/>: площадка с центром <paramref name="center"/>, поворотом <paramref name="rotation"/>
    /// (оси вида) и сторонами <paramref name="extents"/>, м; выход поля <paramref name="field"/> этого кадра.
    /// </summary>
    public void Add(RenderFrame frame, RenderTexture target, Vector4? clear, in Matrix4x4 viewProj, Vector3 center, Quaternion rotation,
        Vector2 extents, RenderTexture field, string name)
    {
        if (frame.Number != _frame)
        {
            _frame = frame.Number;
            _count = _next = 0;
        }
        if (_count == _args.Length) Array.Resize(ref _args, _args.Length * 2);
        var g = frame.Graph;
        _args[_count++] = new Args
        {
            ViewProj = viewProj,
            Center = center,
            U = Vector3.Transform(Vector3.UnitX, rotation) * (0.5f * extents.X),
            V = Vector3.Transform(Vector3.UnitY, rotation) * (0.5f * extents.Y),
            Field = g.SrvOf(field),
        };
        g.AddPass(name, FramePhase.Debug).Color(target, clear).Read(field).Execute(_record);
    }

    private void Record(RenderPassContext ctx)
    {
        ref readonly var a = ref _args[_next++];
        ctx.SetPipeline(Pipeline);
        var m = a.ViewProj;
        ctx.SetConstant(0, m.M11); ctx.SetConstant(1, m.M12); ctx.SetConstant(2, m.M13); ctx.SetConstant(3, m.M14);
        ctx.SetConstant(4, m.M21); ctx.SetConstant(5, m.M22); ctx.SetConstant(6, m.M23); ctx.SetConstant(7, m.M24);
        ctx.SetConstant(8, m.M31); ctx.SetConstant(9, m.M32); ctx.SetConstant(10, m.M33); ctx.SetConstant(11, m.M34);
        ctx.SetConstant(12, m.M41); ctx.SetConstant(13, m.M42); ctx.SetConstant(14, m.M43); ctx.SetConstant(15, m.M44);
        ctx.SetConstant(SlotCx, a.Center.X);
        ctx.SetConstant(SlotCy, a.Center.Y);
        ctx.SetConstant(SlotCz, a.Center.Z);
        ctx.SetConstant(SlotUx, a.U.X);
        ctx.SetConstant(SlotUy, a.U.Y);
        ctx.SetConstant(SlotUz, a.U.Z);
        ctx.SetConstant(SlotVx, a.V.X);
        ctx.SetConstant(SlotVy, a.V.Y);
        ctx.SetConstant(SlotVz, a.V.Z);
        ctx.SetConstant(SlotField, a.Field);
        ctx.SetConstant(SlotOpacity, Opacity);
        ctx.Draw(6);
    }
}
