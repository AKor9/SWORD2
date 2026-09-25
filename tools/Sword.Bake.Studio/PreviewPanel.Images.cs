using System.Globalization;
using System.Numerics;
using Sword.Engine.Ui;

namespace Sword.Bake.Studio;

/// <summary>Картинка предпросмотра в видеопамяти: SRV для <see cref="UiDrawList.Image"/> и владелец (освобождение).</summary>
internal readonly record struct PreviewTexture(int Srv, IDisposable Owner);

/// <summary>
/// Картинки сцены (<see cref="PreviewScene.Images"/>): слева — список с миниатюрами (если картинок больше одной), справа — выбранная.
/// Колесо — приблизить к курсору, ЛКМ — сдвинуть, двойной щелчок — вписать; мип — «авто» по масштабу или выбранный.
/// Под курсором — тексель и его значение. Картинки уходят в видеопамять при первом показе, со своими мипами.
/// </summary>
internal sealed partial class PreviewPanel
{
    private readonly Dictionary<int, PreviewTexture> _textures = [];
    private int _image;
    /// <summary>−1 — мип по масштабу.</summary>
    private int _imageMip = -1;
    private float _imageZoom = 1;
    /// <summary>Сдвиг центра картинки от центра окна, пиксели экрана.</summary>
    private Vector2 _imagePan;
    private Vector2 _imageLastMouse;
    private string[] _mipItems = [];
    private int _mipItemsFor = -1;

    /// <summary>Картинок сцены в видеопамяти (тесты: картинки уходят туда только при показе).</summary>
    internal int UploadedImages => _textures.Count;

    private void ResetImageView()
    {
        _imageZoom = 1;
        _imagePan = Vector2.Zero;
    }

    private void ReleaseImages()
    {
        foreach (var tex in _textures.Values) tex.Owner.Dispose();
        _textures.Clear();
        _mipItemsFor = -1;
    }

    private int Texture(int index)
    {
        if (!_textures.TryGetValue(index, out var tex)) _textures[index] = tex = upload(_scene!.Images[index]);
        return tex.Srv;
    }

    private void DrawImages(UiContext ui, UiRect r)
    {
        var t = ui.Theme;
        var images = _scene!.Images;
        _image = Math.Clamp(_image, 0, images.Count - 1);
        if (images.Count > 1)
        {
            var list = r.SplitLeft(ui.S(210), out r);
            r.SplitLeft(ui.S(8), out r);
            DrawImageList(ui, list, images);
        }

        var current = images[_image];
        var bar = r.SplitBottom(ui.S(30), out var view);
        view.SplitBottom(ui.S(6), out view);
        DrawImageView(ui, view, current, out string? pixel);

        // строка под картинкой: мип, масштаб, пиксель под курсором
        bar = bar.Shrink(0, ui.S(2));
        if (_mipItemsFor != _image)
        {
            _mipItemsFor = _image;
            _mipItems = ["мип: авто", .. Enumerable.Range(0, current.Mips.Count)
                .Select(m => $"мип {m} — {PreviewImage.MipSize(current.Width, m)}×{PreviewImage.MipSize(current.Height, m)}")];
        }
        int mipItem = Math.Clamp(_imageMip + 1, 0, _mipItems.Length - 1);
        if (ui.Combo(bar.SplitLeft(ui.S(190), out bar), "preview-mip", ref mipItem, _mipItems)) _imageMip = mipItem - 1;
        bar.SplitLeft(ui.S(8), out bar);
        if (ui.Button(bar.SplitLeft(ui.S(60), out bar), "1:1", id: "preview-1to1"))
        {
            // тексель мипа 0 — пиксель экрана
            _imageZoom = 1 / MathF.Max(FitScale(view, current), 1e-6f);
            _imagePan = Vector2.Zero;
        }
        bar.SplitLeft(ui.S(6), out bar);
        if (ui.Button(bar.SplitLeft(ui.S(80), out bar), "Вписать", id: "preview-fit")) ResetImageView();
        bar.SplitLeft(ui.S(12), out bar);
        float texelsPerPixel = 1 / MathF.Max(FitScale(view, current) * _imageZoom, 1e-6f);
        string zoom = string.Create(CultureInfo.InvariantCulture, $"{100 / texelsPerPixel:0.#}%");
        float zw = ui.Label(bar, zoom, t.TextDim, UiAlign.Left, UiFace.Body, t.SmallFontSize);
        if (pixel is not null)
            ui.Label(bar with { X0 = bar.X0 + zw + ui.S(16) }, pixel, t.Text, UiAlign.Left, UiFace.Body, t.SmallFontSize);
    }

    /// <summary>Список картинок с миниатюрами.</summary>
    private void DrawImageList(UiContext ui, UiRect list, List<PreviewImage> images)
    {
        var t = ui.Theme;
        float rowH = ui.S(64);
        ui.VirtualList(list, "preview-images", images.Count, rowH, (i, row) =>
        {
            if (ui.Selectable(row, ui.Id(i + 1000), i == _image) && i != _image)
            {
                _image = i;
                ResetImageView();
            }
            var img = images[i];
            var thumb = row.SplitLeft(rowH, out var text).Shrink(ui.S(5));
            ui.Draw.Rect(thumb, UiDrawList.Rgba(16, 16, 20), ui.S(2));
            var fit = FitRect(thumb, DisplayAspect(img));
            float lod = MathF.Log2(MathF.Max(1, img.Width / MathF.Max(fit.Width, 1)));
            ui.Draw.Image(fit, Texture(i), Vector2.Zero, Vector2.One, uint.MaxValue, MathF.Min(lod, img.Mips.Count - 1));
            text = text.Shrink(ui.S(6), ui.S(8));
            ui.Label(text.SplitTop(ui.S(20), out text), img.Name, t.Text, UiAlign.Left, UiFace.Body, t.SmallFontSize);
            ui.Label(text.SplitTop(ui.S(18), out _), img.Caption, t.TextFaint, UiAlign.Left, UiFace.Body, t.SmallFontSize);
        });
    }

    /// <summary>Соотношение сторон при показе.</summary>
    private static float DisplayAspect(PreviewImage img) => img.Aspect > 0 ? img.Aspect : img.Width / (float)Math.Max(1, img.Height);

    /// <summary>Ширина картинки при показе в «текселях»: высота — по пикселям, ширина — по соотношению сторон.</summary>
    private static float DisplayWidth(PreviewImage img) => img.Height * DisplayAspect(img);

    private static float FitScale(UiRect view, PreviewImage img) =>
        MathF.Min(view.Width / MathF.Max(DisplayWidth(img), 1), view.Height / MathF.Max(img.Height, 1)) * 0.94f;

    private static UiRect FitRect(UiRect r, float aspect)
    {
        float w = r.Width, h = r.Height;
        if (w / h > aspect) w = h * aspect;
        else h = w / aspect;
        var c = r.Centre;
        return new UiRect(c.X - w * 0.5f, c.Y - h * 0.5f, c.X + w * 0.5f, c.Y + h * 0.5f);
    }

    private static UiRect Centred(Vector2 centre, Vector2 size) =>
        new(centre.X - size.X * 0.5f, centre.Y - size.Y * 0.5f, centre.X + size.X * 0.5f, centre.Y + size.Y * 0.5f);

    private void DrawImageView(UiContext ui, UiRect view, PreviewImage img, out string? pixel)
    {
        pixel = null;
        ui.Draw.Rect(view, UiDrawList.Rgba(24, 24, 28), ui.S(4));

        float fit = FitScale(view, img);
        float scale = fit * _imageZoom;
        var size = new Vector2(DisplayWidth(img), img.Height) * scale;
        var centre = view.Centre + _imagePan;
        var rect = Centred(centre, size);

        // ввод: колесо — к курсору, ЛКМ — сдвиг, двойной щелчок — вписать
        uint id = ui.Id("preview-image");
        bool hovered = ui.IsHovered(view) && (ui.ActiveId == 0 || ui.ActiveId == id);
        var mouse = ui.Input.Mouse;
        if (hovered && ui.Input.Pressed(0))
        {
            ui.ActiveId = id;
            _imageLastMouse = mouse;
            if (ui.DoubleClicked) ResetImageView();
        }
        if (ui.ActiveId == id)
        {
            if (ui.Input.Down(0))
            {
                _imagePan += mouse - _imageLastMouse;
                _imageLastMouse = mouse;
                ui.Cursor = UiCursor.SizeAll;
            }
            else ui.ActiveId = 0;
        }
        if (hovered && ui.Input.Wheel != 0)
        {
            float zoom = Math.Clamp(_imageZoom * MathF.Pow(1.25f, ui.Input.Wheel), 0.05f, 400f);
            // точка картинки под курсором остаётся под курсором
            var local = (mouse - centre) / scale;
            _imageZoom = zoom;
            scale = fit * _imageZoom;
            _imagePan = mouse - local * scale - view.Centre;
            ui.Input.Wheel = 0;
            size = new Vector2(DisplayWidth(img), img.Height) * scale;
            centre = view.Centre + _imagePan;
            rect = Centred(centre, size);
        }

        ui.Draw.PushClip(view.Shrink(ui.S(1)));
        var visible = rect.Intersect(view);
        if (img.Checker && !visible.IsEmpty) Checker(ui, rect, visible);
        else if (!visible.IsEmpty) ui.Draw.Rect(visible, UiDrawList.Rgba(8, 8, 10));

        float texelsPerPixel = MathF.Max(img.Width / MathF.Max(size.X, 1e-3f), img.Height / MathF.Max(size.Y, 1e-3f));
        float autoMip = Math.Clamp(MathF.Log2(MathF.Max(texelsPerPixel, 1)), 0, img.Mips.Count - 1);
        float mip = _imageMip >= 0 ? Math.Min(_imageMip, img.Mips.Count - 1) : autoMip;
        // тексель показываемого мипа крупнее двух пикселей — квадратами
        float mipTexelPx = (1 << (int)mip) / MathF.Max(texelsPerPixel, 1e-6f);
        ui.Draw.Image(rect, Texture(_image), Vector2.Zero, Vector2.One, uint.MaxValue, mip, 0, nearest: mipTexelPx >= 2);
        ui.Draw.PopClip();

        if (hovered && rect.Contains(mouse))
        {
            int m = (int)mip;
            int mw = PreviewImage.MipSize(img.Width, m), mh = PreviewImage.MipSize(img.Height, m);
            int px = Math.Clamp((int)((mouse.X - rect.X0) / size.X * mw), 0, mw - 1);
            int py = Math.Clamp((int)((mouse.Y - rect.Y0) / size.Y * mh), 0, mh - 1);
            var bytes = img.Mips[m];
            int o = (py * mw + px) * 4;
            pixel = $"мип {m}, x {px}, y {py}: R {bytes[o]}  G {bytes[o + 1]}  B {bytes[o + 2]}  A {bytes[o + 3]}";
        }
        Hint(ui, view, "колесо — приблизить, ЛКМ — сдвинуть, двойной щелчок — вписать");
    }

    /// <summary>Шахматка под прозрачной картинкой — только видимая часть, не больше 40 тысяч клеток.</summary>
    private static void Checker(UiContext ui, UiRect rect, UiRect visible)
    {
        float cell = ui.S(10);
        uint light = UiDrawList.Rgba(70, 70, 76), dark = UiDrawList.Rgba(46, 46, 52);
        ui.Draw.Rect(visible, dark);
        int ix0 = (int)MathF.Floor((visible.X0 - rect.X0) / cell), iy0 = (int)MathF.Floor((visible.Y0 - rect.Y0) / cell);
        int ix1 = (int)MathF.Ceiling((visible.X1 - rect.X0) / cell), iy1 = (int)MathF.Ceiling((visible.Y1 - rect.Y0) / cell);
        if ((long)(ix1 - ix0) * (iy1 - iy0) >= 40000) return;
        for (int y = iy0; y < iy1; y++)
            for (int x = ix0 + ((ix0 + y) & 1); x < ix1; x += 2)
                ui.Draw.Rect(new UiRect(rect.X0 + x * cell, rect.Y0 + y * cell, rect.X0 + (x + 1) * cell, rect.Y0 + (y + 1) * cell).Intersect(visible), light);
    }
}
