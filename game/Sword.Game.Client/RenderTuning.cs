using Sword.Engine.Diagnostics;
using Sword.Engine.Render;

namespace Sword.Game.Client;

/// <summary>
/// Строки ручек рендера во вкладку «Графика» панели F3 (её заводит DevTools: vsync; рендер DevTools не видит — строки добавляет
/// клиент игры, <see cref="ArenaClient.AddGraphicsTuning"/>): сглаживание, тени, GTAO, экспозиция, постобработка. Делегаты пишут
/// <see cref="Renderer"/> напрямую, без команд и реплея; «сохранить» и «загрузить» их не трогают, «сброс» — к значению запуска.
/// </summary>
public static class RenderTuning
{
    public const string Smaa = "smaa", Shadows = "shadows", ShadowSoftness = "shadowSoftness", ShadowSize = "shadowSize",
        PointShadows = "pointShadows", Ao = "ao", AoRadius = "aoRadius", AoIntensity = "aoIntensity", AutoExposure = "autoExposure",
        ExposureCompensation = "exposureCompensation", Vignette = "vignette", Sharpen = "sharpen", Saturation = "saturation",
        Contrast = "contrast", Temperature = "temperature", Taa = "taa", MotionBlur = "motionBlur", TaaBlend = "taaBlend", Shutter = "shutter";

    /// <summary>Строки рендера <paramref name="renderer"/> в группу <paramref name="g"/>.</summary>
    public static void Add(TuningGroup g, Renderer renderer)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(renderer);
        g.Header("СГЛАЖИВАНИЕ");
        var taa = renderer.Taa;
        g.Bool(Taa, "TAA", () => taa.Enabled, v => taa.Enabled = v,
            "временное: камера дрожит на долю пикселя, кадры копятся с историей по глубине; лучше всего убирает мерцание тонких деталей; SMAA при нём не нужен",
            defaultValue: taa.Enabled);
        g.Float(TaaBlend, "доля кадра", () => taa.Blend, v => taa.Blend = v, 0.02f, 0.5f, log: true, format: "0.###",
            hint: "доля нового кадра в смеси: меньше — глаже, но дольше след у быстрого", defaultValue: taa.Blend);
        g.Bool(MotionBlur, "размытие движения", () => taa.MotionBlur, v => taa.MotionBlur = v,
            "размытие мира вдоль его движения на экране от движения камеры (с TAA); персонажи не размываются", defaultValue: taa.MotionBlur);
        g.Float(Shutter, "выдержка", () => taa.Shutter, v => taa.Shutter = v, 0f, 1f, format: "0.##",
            hint: "доля перемещения за кадр, по которой размывается: 0.5 — как кино 180°", defaultValue: taa.Shutter);
        g.Bool(Smaa, "SMAA", () => renderer.Smaa, v => renderer.Smaa = v,
            "SMAA 1x после тонмапа: смягчает лесенки на краях, прямые края и детали не размывает; только с HDR", defaultValue: renderer.Smaa);
        var shadow = renderer.Shadow;
        g.Header("ТЕНИ");
        g.Bool(Shadows, "тени от солнца", () => shadow.Enabled, v => shadow.Enabled = v,
            "карта глубины от солнца над видимой частью земли: тени бойцов, мечей, колонн и рельефа; гасит только солнце", defaultValue: shadow.Enabled);
        g.Float(ShadowSoftness, "мягкость края", () => shadow.Softness, v => shadow.Softness = v, 0.5f, 4f, format: "0.##",
            hint: "шаг выборок PCF 3×3, тексели карты: больше — мягче и шире полутень", defaultValue: shadow.Softness);
        g.Float(ShadowSize, "размер карты", () => shadow.Size, v => shadow.Size = (int)MathF.Round(v / 256) * 256, 512f, 8192f, log: true,
            format: "0", hint: "сторона карты, тексели: больше — чётче тень, дороже память и проход заслоняющих", defaultValue: shadow.Size);
        var lights = renderer.Lights;
        g.Bool(PointShadows, "тени факела", () => lights.Shadows, v => lights.Shadows = v,
            "кубические карты теней точечных источников с флагом (факел): до двух на кадр, по 6 граней 512²; рука держателя тени не даёт",
            defaultValue: lights.Shadows);
        var ao = renderer.Ao;
        g.Header("ЗАТЕНЕНИЕ (GTAO)");
        g.Bool(Ao, "затенение рассеянного", () => ao.Enabled, v => ao.Enabled = v,
            "GTAO по глубине и нормалям: щели, стыки, земля у ног темнее; гасит только небо и контур, не солнце и не факел; выключено — нет и предпрохода",
            defaultValue: ao.Enabled);
        g.Float(AoRadius, "радиус, м", () => ao.Radius, v => ao.Radius = v, 0.1f, 5f, log: true, format: "0.##",
            hint: "как далеко ищутся заслоняющие: больше — шире и мягче тень у стыков", defaultValue: ao.Radius);
        g.Float(AoIntensity, "сила", () => ao.Intensity, v => ao.Intensity = v, 0.2f, 4f, format: "0.##",
            hint: "видимость в этой степени: 1 — как посчитано, больше — темнее", defaultValue: ao.Intensity);
        var auto = renderer.AutoExposure;
        g.Header("ЭКСПОЗИЦИЯ");
        g.Bool(AutoExposure, "автоэкспозиция", () => auto.Enabled, v => auto.Enabled = v,
            "экспозиция — к средней яркости кадра (замер на GPU), плавно: днём как ручная, в темноте светлее до 4 раз; выключена — ручка «экспозиция» вкладки «Свет»",
            defaultValue: auto.Enabled);
        g.Float(ExposureCompensation, "поправка, EV", () => auto.Compensation, v => auto.Compensation = v, -4f, 4f, format: "0.##",
            hint: "сдвиг цели автоэкспозиции: +1 — вдвое светлее", defaultValue: auto.Compensation);
        var post = renderer.Post;
        g.Header("ПОСТОБРАБОТКА");
        g.Float(Vignette, "виньетка", () => post.Vignette, v => post.Vignette = v, 0f, 1f, format: "0.##",
            hint: "затемнение углов кадра до тонмапа: 0 — нет, 1 — углы чёрные", defaultValue: post.Vignette);
        g.Float(Sharpen, "резкость", () => post.Sharpen, v => post.Sharpen = v, 0f, 1f, format: "0.##",
            hint: "адаптивная к контрасту (по мотивам CAS): на резких краях ореолов нет; 0 — прохода нет", defaultValue: post.Sharpen);
        g.Float(Saturation, "насыщенность", () => post.Saturation, v => post.Saturation = v, 0f, 2f, format: "0.##",
            hint: "0 — серое, 1 — как есть", defaultValue: post.Saturation);
        g.Float(Contrast, "контраст", () => post.Contrast, v => post.Contrast = v, 0.5f, 2f, format: "0.##",
            hint: "степень вокруг среднего серого до тонмапа: 1 — как есть", defaultValue: post.Contrast);
        g.Float(Temperature, "баланс белого", () => post.Temperature, v => post.Temperature = v, -1f, 1f, format: "0.##",
            hint: "−1 — холоднее, +1 — теплее при той же яркости", defaultValue: post.Temperature);
    }
}
