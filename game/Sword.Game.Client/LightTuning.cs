using Sword.Engine.Diagnostics;
using Sword.Engine.Render;

namespace Sword.Game.Client;

/// <summary>
/// Вкладка «Свет» панели ручек F3 — ручки клиента, а не симуляции: делегаты читают и пишут значения клиента арены
/// и рендера напрямую, со следующего кадра, без команд и реплея. Ключа в конфиге нет — значения живут до выхода; из
/// командной строки — <c>Sword.exe --tune light.ключ=значение</c>, итог <c>--json</c> — <c>light</c>. Кадр без выделений.
/// </summary>
public static class LightTuning
{
    public const string Id = "light", Title = "Свет";

    /// <summary>Ключи группы (<c>--tune light.ключ=значение</c>, итог <c>--json</c> — <c>light</c>).</summary>
    public const string Night = "night", Exposure = "exposure", Bloom = "bloom", BloomIntensity = "bloomIntensity",
        BloomThreshold = "bloomThreshold", TestLight = "testLight";

    /// <summary>Все ключи группы по порядку строк.</summary>
    public static readonly IReadOnlyList<string> Keys = [Night, TestLight, Exposure, Bloom, BloomIntensity, BloomThreshold];

    /// <summary>Группа «Свет» над клиентом <paramref name="arena"/> и его рендером <paramref name="renderer"/>.</summary>
    public static TuningGroup Build(ArenaClient arena, Renderer renderer)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(renderer);
        var bloom = BloomSettings.Default;
        var g = new TuningGroup(Id, Title);
        g.Header("ОКРУЖЕНИЕ");
        g.Bool(Night, "ночь", () => arena.Night, v => arena.Night = v,
            "тёмная сцена: слабая холодная луна и небо, туман и фон почти чёрные — виден точечный свет; только рендер, в реплей не пишется",
            defaultValue: false);
        g.Bool(TestLight, "тестовый факел у игрока", () => arena.TestLights > 0, v => arena.TestLights = v ? Math.Max(arena.TestLights, 1) : 0,
            "точечный источник «факел LIVE-2» у руки игрока: цвет 1, 0.038, 0; интенсивность 100; экспонента 400; R 20 м (как --test-light)",
            defaultValue: false);
        g.Header("ТОНМАП");
        g.Float(Exposure, "экспозиция", () => renderer.Exposure, v => renderer.Exposure = v, 0.05f, 8f, log: true,
            hint: "множитель цвета сцены перед тонмапом Filmic; 0.99 — средний серый 0.18 выходит как при прежнем выводе",
            defaultValue: Renderer.DefaultExposure);
        g.Header("BLOOM");
        g.Bool(Bloom, "bloom", () => renderer.Bloom.Enabled, v => renderer.Bloom = renderer.Bloom with { Enabled = v },
            "свечение ярких мест (сумма гауссиан UE) — к сцене до экспозиции; выключен — проходов bloom нет", defaultValue: bloom.Enabled);
        g.Float(BloomIntensity, "интенсивность", () => renderer.Bloom.Intensity, v => renderer.Bloom = renderer.Bloom with { Intensity = v },
            0f, 50f, format: "0.##", hint: "сила bloom (Intensity UE); 10 — уровень LIVE-2 с факелом; 0 — проходов нет",
            defaultValue: bloom.Intensity);
        g.Float(BloomThreshold, "порог", () => renderer.Bloom.Threshold, v => renderer.Bloom = renderer.Bloom with { Threshold = v },
            0f, 10f, format: "0.##", hint: "яркость × экспозиция, с которой пиксель начинает светиться (Threshold UE)",
            defaultValue: bloom.Threshold);
        return g;
    }
}
