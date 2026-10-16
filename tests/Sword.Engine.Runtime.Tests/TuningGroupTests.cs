using Sword.Engine.Diagnostics;

namespace Sword.Engine.Runtime.Tests;

/// <summary>
/// Ручки (<see cref="TuningGroup"/>): умолчания, сброс, счёт изменённых, словарь для конфига (кратчайший float, флаг 1/0,
/// прижатие к диапазону), уникальность ключей, строка состояния на стеке.
/// </summary>
public sealed class TuningGroupTests
{
    private sealed class Settings
    {
        public float Damage = 1.5f;
        public float Sharpness = 0.3f;
        public int Enemies = 4;
        public bool Invulnerable;
    }

    private static TuningGroup Group(Settings s) => new TuningGroup("combat", "Бой")
        .Header("Урон")
        .Float("damage", "урон", () => s.Damage, v => s.Damage = v, 0.1f, 10, log: true, hint: "множитель урона")
        .Float("sharpness", "острота", () => s.Sharpness, v => s.Sharpness = v, 0, 1)
        .Int("enemies", "врагов", () => s.Enemies, v => s.Enemies = v, 0, 32)
        .Bool("invulnerable", "игрок неуязвим", () => s.Invulnerable, v => s.Invulnerable = v)
        .Button("сбросить арену", () => { })
        .Info("живых", d => { 7.TryFormat(d, out int n); return n; });

    [Fact]
    public void Defaults_AreValuesAtCreation_ResetAllRestoresThem()
    {
        var s = new Settings();
        var g = Group(s);
        Assert.Equal(7, g.Items.Count);
        Assert.Equal(0, g.Changed());

        s.Damage = 3;
        s.Enemies = 10;
        s.Invulnerable = true;
        Assert.Equal(3, g.Changed());
        Assert.Contains("damage=3", g.Describe());

        g.ResetAll();
        Assert.Equal((1.5f, 4, false), (s.Damage, s.Enemies, s.Invulnerable));
        Assert.Equal(0, g.Changed());
        Assert.Equal("как по умолчанию", g.Describe());
    }

    [Fact]
    public void ExplicitDefault_IsResetTarget_NotCurrentValue()
    {
        var s = new Settings { Damage = 2 };   // значение из конфига, а по умолчанию — из кода
        var g = new TuningGroup("combat", "Бой").Float("damage", "урон", () => s.Damage, v => s.Damage = v, 0.1f, 10, defaultValue: 1);
        Assert.Equal(1, g.Changed());
        g.ResetAll();
        Assert.Equal(1, s.Damage);
    }

    [Fact]
    public void Values_ShortestFloat_BoolAsNumber_ChangedOnly()
    {
        var s = new Settings();
        var g = Group(s);
        var all = g.Values();
        Assert.Equal(["damage", "enemies", "invulnerable", "sharpness"], all.Keys.Order());
        Assert.Equal(0.3, all["sharpness"]);   // 0,3f — это 0,3 в конфиге, а не 0,30000001192092896
        Assert.Equal(4, all["enemies"]);
        Assert.Equal(0, all["invulnerable"]);

        s.Invulnerable = true;
        s.Sharpness = 0.7f;
        var changed = g.Values(changedOnly: true);
        Assert.Equal(["invulnerable", "sharpness"], changed.Keys.Order());
        Assert.Equal(1, changed["invulnerable"]);
        Assert.Equal(0.7f, (float)changed["sharpness"]);
    }

    [Fact]
    public void Values_ApplyRoundTrip_RestoresSameFloats()
    {
        float[] samples = [0.1f, 0.3f, 1f / 3, 2.5e-7f, 123.456f, MathF.PI, 9.999999f];
        foreach (float sample in samples)
        {
            var s = new Settings { Sharpness = sample };
            var g = new TuningGroup("g", "G").Float("x", "x", () => s.Sharpness, v => s.Sharpness = v, 0, 1000);
            var values = g.Values();
            s.Sharpness = 0;
            Assert.Equal(1, g.Apply(values));
            Assert.Equal(sample, s.Sharpness);
        }
    }

    [Fact]
    public void Apply_ClampsRoundsSkipsUnknownAndNonFinite()
    {
        var s = new Settings();
        var g = Group(s);
        var unknown = new List<string>();
        int applied = g.Apply(new Dictionary<string, double>
        {
            ["damage"] = 50,            // выше диапазона — к 10
            ["enemies"] = 3.6,          // целое — округляется
            ["invulnerable"] = 2,       // флаг — «не ноль»
            ["sharpness"] = double.NaN, // не число — пропуск
            ["speed"] = 1,              // незнакомый ключ
        }, unknown);

        Assert.Equal(3, applied);
        Assert.Equal((10f, 4, true, 0.3f), (s.Damage, s.Enemies, s.Invulnerable, s.Sharpness));
        Assert.Equal(["speed"], unknown);
    }

    [Fact]
    public void Int_SetRoundsThroughPanelPath()
    {
        var s = new Settings();
        var g = Group(s);
        var enemies = Assert.IsType<TuningFloat>(g.Find("enemies"));
        Assert.True(enemies.Integer);
        enemies.Set(enemies.Clamp(7.6f));
        Assert.Equal(8, s.Enemies);
        Assert.Equal(32, enemies.Clamp(100));
        Assert.Equal(enemies.Default, enemies.Clamp(float.NaN));
    }

    [Fact]
    public void DuplicateKey_BadRange_AreErrors()
    {
        var s = new Settings();
        var g = new TuningGroup("g", "G").Float("x", "x", () => s.Damage, v => s.Damage = v, 0, 1);
        Assert.Throws<ArgumentException>(() => g.Bool("x", "флаг", () => s.Invulnerable, v => s.Invulnerable = v));
        Assert.Throws<ArgumentException>(() => g.Float("y", "y", () => s.Damage, v => s.Damage = v, 1, 1));
        Assert.Throws<ArgumentException>(() => g.Float("z", "z", () => s.Damage, v => s.Damage = v, 0, float.PositiveInfinity));
        // пустой ключ — не сохраняется и не мешает другим пустым
        g.Float("", "без ключа", () => s.Damage, v => s.Damage = v, 0, 1).Float("", "ещё", () => s.Damage, v => s.Damage = v, 0, 1);
        Assert.DoesNotContain("", g.Values().Keys);
        Assert.Null(g.Find(""));
    }

    [Fact]
    public void Info_WritesToStack_StringOverloadTruncates()
    {
        var g = new TuningGroup("g", "G")
            .Info("стек", d => { 42.TryFormat(d, out int n); return n; })
            .Info("строка", () => "длинная строка состояния");
        Span<char> buffer = stackalloc char[7];
        var stack = Assert.IsType<TuningInfo>(g.Items[0]);
        Assert.Equal("42", buffer[..stack.Text(buffer)].ToString());
        var text = Assert.IsType<TuningInfo>(g.Items[1]);
        Assert.Equal("длинная", buffer[..text.Text(buffer)].ToString());
    }
}
