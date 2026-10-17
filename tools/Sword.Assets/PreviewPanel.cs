using System.Numerics;
using Sword.Assets.Preview;
using Sword.Engine.Content;
using Sword.Engine.Ui;

namespace Sword.Assets;

/// <summary>
/// Предпросмотр выбранного в <c>Content/</c>: сцена по виду файла (<see cref="Previews"/>).
/// 3D-сцена рисуется интерфейсом: треугольники с плоским светом, сортировка по глубине (художник), линии поверх. ЛКМ — вращать,
/// колесо — приблизить, двойной щелчок — вид по умолчанию. Картинки — через <paramref name="upload"/> в видеопамять
/// (<see cref="DrawImages"/>), анимация — список клипов и проигрыватель над той же 3D-сценой (<see cref="DrawAnimation"/>).
/// Файлы читаются при смене выбора или их изменении. Перенос <c>PreviewPanel</c> Studio SWORD 2eab13b.
/// </summary>
/// <param name="upload">Картинка сцены → SRV для <see cref="UiDrawList.Image"/> и её владелец (окно — <c>Renderer.CreateImage</c>).</param>
/// <param name="mode">Вариант показа при открытии (<c>--preview-mode</c>).</param>
/// <param name="still">Клип открывается на паузе в начале (снимки <c>--shot</c>/<c>--gallery</c>: кадр не зависит от времени).</param>
internal sealed partial class PreviewPanel(Func<PreviewImage, PreviewTexture> upload, int mode = 0, bool still = false) : IDisposable
{
    private PreviewScene? _scene;
    private string _key = "";
    private string? _error;
    private int _mode = mode;
    private Vector3 _centre, _bounds;
    private float _yaw = 0.6f, _pitch = 0.35f, _zoom = 1;
    /// <summary>Вид по умолчанию (<see cref="FitView"/>) и масштаб, при котором сцена заполняет окно.</summary>
    private float _fitYaw = 0.6f, _fitPitch = 0.35f, _fitScale = 1;
    private float _fitAspect = 1, _fitRoll, _roll;
    private Vector2 _lastMouse;

    /// <summary>Открытая сцена-анимация забирает ↑/↓ для выбора клипа, а не файла в дереве.</summary>
    public bool TakesVerticalKeys => _scene?.Animation is not null;

    /// <summary>Текущая сцена (тесты и отладка): null — нечего смотреть или ошибка (<see cref="Error"/>).</summary>
    internal PreviewScene? Scene => _scene;

    internal string? Error => _error;

    /// <summary>Треугольников, нарисованных последним кадром 3D-сцены (после отсечения задних граней и вырожденных).</summary>
    internal int LastDrawnTriangles { get; private set; }

    private struct Projected
    {
        public Vector2 A, B, C;
        public float Depth;
        public uint Colour;
    }

    private Projected[] _tris = [];
    private int[] _order = [];
    private float[] _depthKeys = [];

    /// <summary>Картинки — в отложенное освобождение.</summary>
    public void Dispose()
    {
        ReleaseImages();
        _scene = null;
        _key = "";
    }

    /// <param name="selection">Выбранный файл или каталог <c>Content/</c> — ключ вида (смена выбора ставит вид заново).</param>
    /// <param name="files">Файлы выбора.</param>
    public void Draw(UiContext ui, UiRect r, IContentSource content, string selection, IReadOnlyList<ContentFile> files)
    {
        var t = ui.Theme;
        if (files.Count == 0)
        {
            ui.Label(r.SplitTop(ui.S(28), out _), "Пусто: в Content/ нет файлов — испеките их на вкладке «Пекарня».", t.TextFaint);
            return;
        }
        var stamp = new HashCode();
        foreach (var f in files)
        {
            stamp.Add(f.Path);
            stamp.Add(f.Size);
            stamp.Add(f.ModifiedUtc);
        }
        string key = selection + "|" + _mode + "|" + stamp.ToHashCode();
        if (key != _key) Load(content, selection, files.Select(f => f.Path).ToList(), key);

        if (_scene is { Modes.Length: > 1 } s)
        {
            int mode = _mode;
            ui.Tabs(r.SplitTop(ui.S(28), out r), "preview-modes", ref mode, s.Modes);
            if (mode != _mode) _mode = mode;   // сцена пересоберётся в следующем кадре
            r.SplitTop(ui.S(6), out r);
        }
        if (_error is not null)
        {
            ui.Label(r.SplitTop(ui.S(28), out _), _error, t.Bad);
            return;
        }
        if (_scene is null)
        {
            ui.Label(r.SplitTop(ui.S(28), out _), "Для этого вида файлов предпросмотра нет.", t.TextFaint);
            return;
        }

        // легенда — в колонки, если строк много (13 секций органов), чтобы сведения под ней не обрезались
        int legendColumns = Math.Clamp((int)(r.Width / ui.S(300)), 1, Math.Max(1, (_scene.Legend.Count + 5) / 6));
        int legendRows = (_scene.Legend.Count + legendColumns - 1) / legendColumns;
        int infoLines = _scene.Info.Count + legendRows + _animStatus.Count;
        float infoH = infoLines == 0 ? 0 : ui.S(18) * infoLines + ui.S(10);
        bool textOnly = _scene.Images.Count == 0 && _scene.Animation is null && _scene.Triangles.Count == 0 && _scene.Lines.Count == 0;
        var info = r.SplitBottom(textOnly ? r.Height : MathF.Min(infoH, r.Height * 0.4f), out var view);

        if (_scene.Images.Count > 0) DrawImages(ui, view);
        else if (_scene.Animation is not null) DrawAnimation(ui, view);
        else if (!textOnly) Draw3D(ui, view);

        info.SplitTop(ui.S(6), out info);
        ui.Draw.PushClip(info);
        if (legendRows > 0) DrawLegend(ui, info.SplitTop(ui.S(18) * legendRows, out info), legendColumns, legendRows);
        foreach (var line in _scene.Info)
            ui.Label(info.SplitTop(ui.S(18), out info), line, t.TextDim, UiAlign.Left, UiFace.Body, t.SmallFontSize);
        if (_scene.Animation is not null)
            foreach (var line in _animStatus)
                ui.Label(info.SplitTop(ui.S(18), out info), line, t.TextDim, UiAlign.Left, UiFace.Body, t.SmallFontSize);
        ui.Draw.PopClip();
    }

    private void DrawLegend(UiContext ui, UiRect legend, int columns, int rows)
    {
        var t = ui.Theme;
        float columnW = legend.Width / columns;
        for (int i = 0; i < _scene!.Legend.Count; i++)
        {
            var (colour, text) = _scene.Legend[i];
            int col = i / rows, row = i % rows;
            var cell = UiRect.FromSize(legend.X0 + col * columnW, legend.Y0 + row * ui.S(18), columnW, ui.S(18));
            var swatch = cell.SplitLeft(ui.S(14), out cell);
            ui.Draw.Rect(swatch.Shrink(ui.S(1), ui.S(3)), colour, ui.S(2));
            ui.Label(cell.Shrink(ui.S(6), 0), text, t.Text, UiAlign.Left, UiFace.Body, t.SmallFontSize);
        }
    }

    private void Load(IContentSource content, string selection, List<string> paths, string key)
    {
        bool sameAsset = _key.Split('|')[0] == selection;
        _key = key;
        _error = null;
        _scene = null;
        ReleaseImages();
        _clipFilterSeen = null;   // клипы новой сцены могли измениться (перепечены) — отфильтровать заново
        if (!sameAsset) ResetAssetState();
        try
        {
            _scene = Previews.Build(content, paths, _mode);
            if (_scene?.Animation is { } anim) PoseAnimation(anim, force: true);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _error = "Не удалось прочитать: " + e.Message;
            _scene = null;
        }
        if (_scene is null) return;
        if (_mode >= Math.Max(1, _scene.Modes.Length)) _mode = 0;
        // рамка сцены — один раз на ассет: смена режима не сбрасывает вид
        if (sameAsset && _tris.Length > 0) return;
        var (min, max) = SceneBounds(_scene);
        _centre = min.X <= max.X ? (min + max) * 0.5f : Vector3.Zero;
        _bounds = min.X <= max.X ? max - min : Vector3.Zero;
        _zoom = 1;
        FitView();
    }

    /// <summary>Рамка сцены: заданная обработчиком или по треугольникам и линиям.</summary>
    private static (Vector3 Min, Vector3 Max) SceneBounds(PreviewScene scene)
    {
        if (scene.Bounds is { } b) return b;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var tri in scene.Triangles)
        {
            min = Vector3.Min(min, Vector3.Min(tri.A, Vector3.Min(tri.B, tri.C)));
            max = Vector3.Max(max, Vector3.Max(tri.A, Vector3.Max(tri.B, tri.C)));
        }
        foreach (var l in scene.Lines)
        {
            min = Vector3.Min(min, Vector3.Min(l.A, l.B));
            max = Vector3.Max(max, Vector3.Max(l.A, l.B));
        }
        return (min, max);
    }

    /// <summary>Другой ассет: вид, выбор картинки и клипа — заново.</summary>
    private void ResetAssetState()
    {
        _tris = [];
        _image = 0;
        _imageMip = -1;
        ResetImageView();
        _clip = 0;
        _clipTime = 0;
        _scrollClipTo = -1;
        _clipFilter = "";
        _playing = !still;
        _animStatus.Clear();
    }

    /// <summary>
    /// Ракурс, в котором сцена видна крупнее всего (у клинка — плашмя, вдоль экрана): перебор углов по площади проекции;
    /// масштаб — чтобы проекция заняла окно. Точки — не больше пары тысяч вершин. Сцена «стоя» (персонаж) — спереди-сбоку.
    /// </summary>
    private void FitView()
    {
        var pts = new List<Vector3>();
        if (_scene!.Bounds is null)
        {
            int step = Math.Max(1, _scene.Triangles.Count / 700);
            for (int i = 0; i < _scene.Triangles.Count; i += step)
            {
                var tri = _scene.Triangles[i];
                pts.Add(tri.A - _centre);
                pts.Add(tri.B - _centre);
                pts.Add(tri.C - _centre);
            }
            foreach (var l in _scene.Lines)
            {
                pts.Add(l.A - _centre);
                pts.Add(l.B - _centre);
            }
            if (pts.Count == 0) return;
        }
        // углы рамки: выборка треугольников может пропустить крайние точки (макушку персонажа)
        var half = _bounds * 0.5f;
        for (int k = 0; k < 8; k++)
            pts.Add(new Vector3((k & 1) != 0 ? half.X : -half.X, (k & 2) != 0 ? half.Y : -half.Y, (k & 4) != 0 ? half.Z : -half.Z));
        if (_scene.Upright)
        {
            _fitYaw = -0.55f;
            _fitPitch = 0.12f;
            _fitRoll = 0;
        }
        else BestAngle(pts);
        var fit = View(_fitYaw, _fitPitch, _fitRoll);
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var q in pts)
        {
            var v = Vector3.Transform(q, fit);
            min = Vector2.Min(min, new Vector2(v.X, v.Y));
            max = Vector2.Max(max, new Vector2(v.X, v.Y));
        }
        var size = max - min;
        _fitScale = 0.9f / MathF.Max(size.X, 1e-4f);
        _fitAspect = size.Y / MathF.Max(size.X, 1e-4f);
        _yaw = _fitYaw;
        _pitch = _fitPitch;
        _roll = _fitRoll;
    }

    /// <summary>
    /// Ракурс по площади проекции (определитель ковариации не зависит от поворота в плоскости экрана), потом поворот
    /// в плоскости: главная ось проекции — горизонтально.
    /// </summary>
    private void BestAngle(List<Vector3> pts)
    {
        float best = -1;
        for (int y = 0; y < 24; y++)
            for (int p = -3; p <= 3; p++)
            {
                float yaw = y * MathF.PI / 12, pitch = p * 0.35f;
                var rot = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateRotationX(pitch);
                double xx = 0, xy = 0, yy = 0;
                foreach (var q in pts)
                {
                    var v = Vector3.Transform(q, rot);
                    xx += v.X * v.X; xy += v.X * v.Y; yy += v.Y * v.Y;
                }
                float score = (float)(xx * yy - xy * xy) * (1 - 0.05f * MathF.Abs(p));
                if (score <= best) continue;
                best = score;
                _fitYaw = yaw;
                _fitPitch = pitch;
                _fitRoll = -0.5f * MathF.Atan2((float)(2 * xy), (float)(xx - yy));
            }
    }

    /// <summary>Камера: поворот вокруг вертикали, наклон, поворот в плоскости экрана.</summary>
    private static Matrix4x4 View(float yaw, float pitch, float roll) =>
        Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateRotationX(pitch) * Matrix4x4.CreateRotationZ(roll);

    /// <summary>3D-сцена в прямоугольнике: фон, ввод, треугольники, подсказка.</summary>
    private void Draw3D(UiContext ui, UiRect view)
    {
        ui.Draw.Rect(view, ui.Theme.Viewport, ui.S(4));
        HandleInput(ui, view);
        ui.Draw.PushClip(view);
        DrawScene(ui, view);
        ui.Draw.PopClip();
        Hint(ui, view, "ЛКМ — вращать, колесо — приблизить, двойной щелчок — сброс");
    }

    /// <summary>Подсказка управления в левом верхнем углу окна просмотра — на подложке, чтобы читалась поверх картинки.</summary>
    private static void Hint(UiContext ui, UiRect view, string text)
    {
        var t = ui.Theme;
        float fs = ui.S(t.SmallFontSize), pad = ui.S(4);
        var row = view.Shrink(ui.S(8)).SplitTop(ui.S(18), out _);
        float w = ui.Draw.TextBoxSize(UiFace.Body, fs, text, new Vector2(pad, 0)).X;
        var box = new UiRect(row.X0 - pad, row.Y0, MathF.Min(row.X0 - pad + w, row.X1), row.Y1);
        ui.Draw.TextBox(box, UiFace.Body, fs, t.TextDim, text, t.TextBackdrop, pad, ui.S(3));
    }

    private void HandleInput(UiContext ui, UiRect view)
    {
        var mouse = ui.Input.Mouse;
        bool dragging = ui.Drag(view, ui.Id("preview-view"), out _, out bool started);
        if (started)
        {
            _lastMouse = mouse;
            if (ui.DoubleClicked)
            {
                _yaw = _fitYaw;
                _pitch = _fitPitch;
                _roll = _fitRoll;
                _zoom = 1;
            }
        }
        if (dragging)
        {
            var d = mouse - _lastMouse;
            _yaw -= d.X * 0.01f;
            _pitch = Math.Clamp(_pitch + d.Y * 0.01f, -1.5f, 1.5f);
            _lastMouse = mouse;
            ui.Cursor = UiCursor.SizeAll;
        }
        if (ui.TakeWheel(view) is var wheel and not 0) _zoom = Math.Clamp(_zoom * MathF.Pow(1.15f, wheel), 0.2f, 20f);
    }

    private void DrawScene(UiContext ui, UiRect view)
    {
        var scene = _scene!;
        var rot = View(_yaw, _pitch, _roll);
        // по ширине окна, но так, чтобы и высота влезла
        float scale = MathF.Min(view.Width, view.Height / MathF.Max(_fitAspect, 1e-3f)) * _fitScale * _zoom;
        var c = view.Centre;
        var light = Vector3.Normalize(new Vector3(-0.4f, 0.6f, 0.7f));

        int n = scene.Triangles.Count;
        if (_tris.Length < n)
        {
            _tris = new Projected[n];
            _order = new int[n];
            _depthKeys = new float[n];
        }
        int count = 0;
        foreach (var tri in scene.Triangles)
            if (Project(tri, rot, c, scale, light, scene.CullBackFaces, out _tris[count]))
            {
                _order[count] = count;
                // вращение с положительным Z к зрителю — дальние (меньший Z) рисуются первыми
                _depthKeys[count] = _tris[count].Depth;
                count++;
            }
        Array.Sort(_depthKeys, _order, 0, count);
        for (int i = 0; i < count; i++)
        {
            ref var p = ref _tris[_order[i]];
            // сглаженные края соседних треугольников просвечивают фоном — шов; раздвинуть вершины на полпикселя от центра
            var g = (p.A + p.B + p.C) / 3;
            ui.Draw.Triangle(Grow(p.A, g), Grow(p.B, g), Grow(p.C, g), p.Colour);
        }
        LastDrawnTriangles = count;
        float thick = MathF.Max(1, ui.S(1.2f));
        foreach (var l in scene.Lines)
            ui.Draw.Line(ToScreen(ToView(l.A, rot), c, scale), ToScreen(ToView(l.B, rot), c, scale), thick, l.Colour);
    }

    private Vector3 ToView(Vector3 p, Matrix4x4 rot) => Vector3.Transform(p - _centre, rot);

    private static Vector2 ToScreen(Vector3 v, Vector2 c, float scale) => new(c.X + v.X * scale, c.Y - v.Y * scale);

    /// <summary>Треугольник на экран: false — задняя грань (если отсекаются) или меньше 0,02 пикселя.</summary>
    private bool Project(in PreviewTriangle tri, Matrix4x4 rot, Vector2 c, float scale, Vector3 light, bool cull, out Projected p)
    {
        p = default;
        var a = ToView(tri.A, rot);
        var b = ToView(tri.B, rot);
        var cc = ToView(tri.C, rot);
        var nrm = Vector3.Cross(b - a, cc - a);
        // вращение без отражения, +Z к зрителю: лицевая грань (против часовой) смотрит на +Z
        if (cull && nrm.Z <= 0) return false;
        var sa = ToScreen(a, c, scale);
        var sb = ToScreen(b, c, scale);
        var sc = ToScreen(cc, c, scale);
        float area = MathF.Abs((sb.X - sa.X) * (sc.Y - sa.Y) - (sc.X - sa.X) * (sb.Y - sa.Y));
        if (area < 0.02f) return false;
        float shade = nrm.LengthSquared() > 1e-20f ? 0.35f + 0.65f * MathF.Abs(Vector3.Dot(Vector3.Normalize(nrm), light)) : 0.6f;
        p = new Projected { A = sa, B = sb, C = sc, Depth = (a.Z + b.Z + cc.Z) / 3, Colour = Shade(tri.Colour, shade) };
        return true;
    }

    private static Vector2 Grow(Vector2 v, Vector2 centre)
    {
        var d = v - centre;
        float len = d.Length();
        return len > 1e-4f ? v + d * (0.6f / len) : v;
    }

    private static uint Shade(uint colour, float k)
    {
        uint r = (uint)MathF.Min(255, (colour & 0xFF) * k), g = (uint)MathF.Min(255, ((colour >> 8) & 0xFF) * k),
            b = (uint)MathF.Min(255, ((colour >> 16) & 0xFF) * k);
        return r | g << 8 | b << 16 | (colour & 0xFF000000);
    }
}
