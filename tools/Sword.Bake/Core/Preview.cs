using System.Numerics;
using Sword.Engine.Content;

namespace Sword.Bake;

/// <summary>
/// Обработчик, выходы которого можно посмотреть в Studio (вкладка «Просмотр»): строит из выходов ассета простую 3D-сцену,
/// набор картинок или анимированную сцену. Ядро и Studio не знают форматов игры — только треугольники, линии, пиксели и подписи.
/// </summary>
public interface IAssetPreview
{
    /// <param name="content">Пак целиком (пути как в паке). Источник годен и после возврата (им пользуется
    /// <see cref="PreviewAnimation"/>), но пак между кадрами закрывается: поток из OpenRead дочитывать сразу, не хранить.</param>
    /// <param name="paths">Пути выходов этого ассета.</param>
    /// <param name="mode">Выбранный вариант показа — индекс в <see cref="PreviewScene.Modes"/> прошлой сцены (0 — первый).</param>
    /// <returns>Сцена; null — смотреть нечего (например, нужного выхода нет).</returns>
    PreviewScene? Preview(IContentSource content, IReadOnlyList<string> paths, int mode);
}

public readonly record struct PreviewTriangle(Vector3 A, Vector3 B, Vector3 C, uint Colour);

public readonly record struct PreviewLine(Vector3 A, Vector3 B, uint Colour);

/// <summary>
/// Сцена предпросмотра: мир Y вверх, метры. Цвет — RGBA в байтах little-endian (R — младший, как UiDrawList.Rgba).
/// Есть <see cref="Images"/> — Studio показывает картинки вместо 3D; есть <see cref="Animation"/> — треугольники и линии
/// сцены — поза, которую анимация перестраивает во времени.
/// </summary>
public sealed class PreviewScene
{
    public List<PreviewTriangle> Triangles { get; } = [];
    public List<PreviewLine> Lines { get; } = [];
    /// <summary>Картинки (слои текстуры, лист шрифта): непусто — показываются они, а не 3D-сцена.</summary>
    public List<PreviewImage> Images { get; } = [];
    /// <summary>Подписи цветов.</summary>
    public List<(uint Colour, string Text)> Legend { get; } = [];
    /// <summary>Строки сведений.</summary>
    public List<string> Info { get; } = [];
    /// <summary>Варианты показа — переключатель в Studio; пусто — один вариант.</summary>
    public string[] Modes { get; init; } = [];
    /// <summary>Клипы и поза во времени; null — сцена неподвижна.</summary>
    public PreviewAnimation? Animation { get; init; }
    /// <summary>Вертикаль сцены остаётся вертикалью экрана (персонаж): вид — спереди-сбоку, без подбора наклона и поворота в плоскости экрана.</summary>
    public bool Upright { get; init; }
    /// <summary>Рамка, по которой ставится вид (персонаж без следа root motion); null — по треугольникам и линиям.</summary>
    public (Vector3 Min, Vector3 Max)? Bounds { get; set; }
    /// <summary>Замкнутый меш с гранями против часовой стрелки снаружи: задние грани не рисуются.</summary>
    public bool CullBackFaces { get; init; }

    public static uint Rgb(byte r, byte g, byte b, byte a = 255) => r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);
}

/// <summary>
/// Картинка предпросмотра: RGBA8 без премножения, ряды сверху вниз, с цепочкой мипов (мип i — max(1, W≫i) × max(1, H≫i)).
/// </summary>
public sealed class PreviewImage
{
    public required string Name { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>Пиксели мипов, начиная с полного размера; хотя бы один.</summary>
    public required IReadOnlyList<byte[]> Mips { get; init; }
    /// <summary>Соотношение ширины к высоте при показе (слой, приведённый к квадрату, показывается в исходной пропорции); 0 — как у пикселей.</summary>
    public float Aspect { get; init; }
    /// <summary>Подпись под картинкой (размер, пропорция).</summary>
    public string Caption { get; init; } = "";
    /// <summary>Фон под прозрачными пикселями — шахматка (иначе тёмный).</summary>
    public bool Checker { get; init; }

    public static int MipSize(int size, int mip) => Math.Max(1, size >> mip);

    /// <summary>Цепочка мипов усреднением 2×2 (для картинок, у которых своих мипов нет).</summary>
    public static List<byte[]> BuildMips(byte[] rgba, int width, int height)
    {
        var mips = new List<byte[]> { rgba };
        int w = width, h = height;
        while (w > 1 || h > 1)
        {
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var src = mips[^1];
            var dst = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    int x1 = Math.Min(x * 2 + 1, w - 1), y1 = Math.Min(y * 2 + 1, h - 1);
                    int a = (y * 2 * w + x * 2) * 4, b = (y * 2 * w + x1) * 4, c = (y1 * w + x * 2) * 4, d = (y1 * w + x1) * 4;
                    for (int k = 0; k < 4; k++)
                        dst[(y * nw + x) * 4 + k] = (byte)((src[a + k] + src[b + k] + src[c + k] + src[d + k] + 2) >> 2);
                }
            mips.Add(dst);
            w = nw;
            h = nh;
        }
        return mips;
    }
}

public readonly record struct PreviewClip(string Name, float Duration, float FrameRate, bool Loop);

/// <summary>
/// Анимация предпросмотра: список клипов и поза. Studio держит выбранный клип и время, а сцену перестраивает
/// через <see cref="Pose"/> каждый кадр проигрывания. Данные клипа грузятся лениво — по выбору.
/// </summary>
public abstract class PreviewAnimation
{
    public abstract IReadOnlyList<PreviewClip> Clips { get; }

    /// <summary>
    /// Поставить позу клипа <paramref name="clip"/> в момент <paramref name="time"/>, с: очистить и заново заполнить
    /// треугольники и линии <paramref name="scene"/>. Сведения о клипе (кадр, смещение корня) — в <paramref name="status"/>.
    /// </summary>
    public abstract void Pose(int clip, float time, PreviewScene scene, List<string> status);
}
