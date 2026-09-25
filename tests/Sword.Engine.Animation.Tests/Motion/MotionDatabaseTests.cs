using System.Numerics;
using System.Reflection;
using Sword.Engine.Animation.Motion;
using static Sword.Engine.Animation.Tests.MotionFixtures;

namespace Sword.Engine.Animation.Tests;

/// <summary>
/// База Motion Matching из готовых массивов (контракт 8а — по нему тестируют 8в и 8г, пока 8б строит базу из клипов): общая и
/// неизменяемая — наружу ничего записываемого, вход копируется; строки поиска, блоки, отпечаток и сводка выводятся из данных;
/// противоречивые данные отвергаются.
/// </summary>
public sealed class MotionDatabaseTests
{
    /// <summary>Как <c>AnimationTests.SharedAnimationAssets_ExposeNoWritableData</c>: ни изменяемых массивов наружу, ни открытых полей, ни сеттеров.</summary>
    [Theory]
    [InlineData(typeof(MotionDatabase))]
    [InlineData(typeof(MotionSchema))]
    public void SharedMotionAssets_ExposeNoWritableData(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var problems = new List<string>();
        foreach (var f in type.GetFields(all))
            if (!f.IsLiteral && (!f.IsInitOnly || f.FieldType.IsArray)) problems.Add($"поле {f.Name}");
        foreach (var p in type.GetProperties(all))
        {
            if (p.SetMethod is { IsPublic: true }) problems.Add($"сеттер {p.Name}");
            if (p.PropertyType.IsArray || p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(Span<>))
                problems.Add($"свойство {p.Name}: {p.PropertyType.Name}");
        }
        foreach (var m in type.GetMethods(all).Where(m => !m.IsSpecialName))
            if (m.ReturnType.IsArray || m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(Span<>))
                problems.Add($"метод {m.Name}: {m.ReturnType.Name}");
        Assert.True(problems.Count == 0, $"{type.Name}: " + string.Join(", ", problems));
    }

    [Fact]
    public void FromArrays_CopiesInputAndDerivesRowsBlocksSummary()
    {
        var lib = Library();
        var schema = Schema();
        var a = Plain(lib, schema, [0, 1, 2]);
        a.Entry[29] = 3;   // ранний выход нециклического клипа (кадры 20…31) во вход цикла
        a.Next[29] = a.Next[3];
        a.Interval[29] = a.Interval[3];
        var db = a.Build(lib, schema);
        var features = a.Features.ToArray();

        // вход копируется: правка массивов после постройки базу не меняет
        a.Features[0] += 1;
        a.Next[0] = -1;
        a.Searchable[0] = false;
        Assert.Equal(features, db.Features.ToArray());
        Assert.Equal(1, db.Next[0]);
        Assert.True(db.Searchable[0]);

        Assert.Equal(48, db.FrameCount);
        Assert.Equal(3, db.ClipCount);
        Assert.Equal(schema.FeatureCount, db.FeatureCount);
        Assert.Equal(1, db.ClipOf[20]);
        Assert.Equal(lib.Clips[1].Start + 5, db.LibraryFrame(25));
        Assert.Equal(5, db.FrameInClip(25));
        Assert.Equal(2, db.FindClip(2));
        Assert.Equal(-1, db.FindClip(7));

        // строки поиска — искомые кадры по возрастанию: без последних кадров циклов и последнего кадра нециклического
        int[] rows = [.. Enumerable.Range(0, 48).Where(f => f is not (19 or 31 or 47))];
        Assert.Equal(rows, db.Rows.ToArray());
        Assert.Equal((rows.Length + 15) / 16, db.BlockCount);
        int fc = db.FeatureCount;
        for (int b = 0; b < db.BlockCount; b++)
        {
            var frames = db.BlockRows(b);
            Assert.Equal(rows.Skip(b * 16).Take(16), frames.ToArray());
            for (int i = 0; i < fc; i++)
            {
                float min = float.MaxValue, max = float.MinValue;
                foreach (int f in frames)
                {
                    min = MathF.Min(min, db.FeatureRow(f)[i]);
                    max = MathF.Max(max, db.FeatureRow(f)[i]);
                }
                Assert.Equal(min, db.BoundsMin[b * fc + i]);
                Assert.Equal(max, db.BoundsMax[b * fc + i]);
            }
        }

        Assert.Equal(new MotionDatabaseSummary(3, 48, rows.Length, 1, 1, db.Summary.BuildMilliseconds), db.Summary);
        Assert.True(db.Summary.BuildMilliseconds >= 0);

        var raw = new float[fc];
        db.RawFeatures(7, raw);
        for (int i = 0; i < fc; i++) Assert.Equal(db.FeatureRow(7)[i] * db.Std[i] + db.Mean[i], raw[i]);
        Assert.Equal(db.Contacts[4] != 0, db.Contact(4, 0));
        Assert.True(db.Contact(4, 0));
        Assert.False(db.Contact(0, 0));
    }

    [Fact]
    public void Fingerprint_SameDataSame_AnyChangeDiffers()
    {
        var lib = Library();
        var schema = Schema();
        ulong Print(Action<Arrays>? change = null)
        {
            var a = Plain(lib, schema, [0, 1, 2]);
            change?.Invoke(a);
            return a.Build(lib, schema).Fingerprint;
        }
        ulong baseline = Print();
        Assert.Equal(baseline, Print());
        Assert.NotEqual(baseline, Print(a => a.Features[100] = MathF.BitIncrement(a.Features[100])));
        Assert.NotEqual(baseline, Print(a => a.Next[5] = 7));
        Assert.NotEqual(baseline, Print(a => a.Entry[30] = 2));
        Assert.NotEqual(baseline, Print(a => a.Searchable[5] = false));
        Assert.NotEqual(baseline, Print(a => a.Interval[5] = a.Interval[5] with { Yaw = a.Interval[5].Yaw + 1e-6f }));
        Assert.NotEqual(baseline, Print(a => a.Std[2] = 2));
        Assert.NotEqual(baseline, Print(a => a.Contacts[0] = 1));
        Assert.NotEqual(baseline, Print(a => a.MovingJoints = [Root]));
        Assert.NotEqual(Plain(lib, schema, [0, 2]).Build(lib, schema).Fingerprint, Plain(lib, schema, [0, 1]).Build(lib, schema).Fingerprint);
        // −0 и +0 — одно число
        Assert.Equal(Print(a => a.Features[3] = 0f), Print(a => a.Features[3] = -0f));
    }

    [Theory]
    [InlineData("клипы не по возрастанию")]
    [InlineData("клип не целиком")]
    [InlineData("кадры не подряд")]
    [InlineData("нет клипов")]
    [InlineData("длина признаков")]
    [InlineData("следующий вне базы")]
    [InlineData("вход вне базы")]
    [InlineData("искомый без следующего")]
    [InlineData("признак не число")]
    [InlineData("разброс ноль")]
    [InlineData("перенос не число")]
    [InlineData("контакт сверх суставов")]
    [InlineData("движущиеся не по возрастанию")]
    [InlineData("сустав схемы вне скелета")]
    public void FromArrays_RejectsInconsistentData(string what)
    {
        var lib = Library();
        var schema = Schema();
        var a = Plain(lib, schema, [0, 1, 2]);
        var s = schema;
        switch (what)
        {
            case "клипы не по возрастанию": (a.Clips[0], a.Clips[1]) = (a.Clips[1] with { Start = 0 }, a.Clips[0] with { Start = 12 }); break;
            case "клип не целиком": a.Clips[2] = a.Clips[2] with { Count = 15 }; break;
            case "кадры не подряд": a.Clips[1] = a.Clips[1] with { Start = 21 }; break;
            case "нет клипов": a.Clips = []; break;
            case "длина признаков": a.Features = a.Features[..^1]; break;
            case "следующий вне базы": a.Next[3] = 48; break;
            case "вход вне базы": a.Entry[3] = -2; break;
            case "искомый без следующего": a.Next[3] = -1; break;
            case "признак не число": a.Features[10] = float.NaN; break;
            case "разброс ноль": a.Std[1] = 0; break;
            case "перенос не число": a.Interval[2] = a.Interval[2] with { X = float.PositiveInfinity }; break;
            case "контакт сверх суставов": a.Contacts[2] = 2; break;
            case "движущиеся не по возрастанию": a.MovingJoints = [Pelvis, Root]; break;
            case "сустав схемы вне скелета":
                s = new MotionSchema([], [], [new("j", FeatureKind.JointPosition, 1, 0.1f, [3])]);
                a = Plain(lib, s, [0, 1, 2]);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(what), what, "нет такого случая");
        }
        Assert.ThrowsAny<ArgumentException>(() => a.Build(lib, s));
    }
}
