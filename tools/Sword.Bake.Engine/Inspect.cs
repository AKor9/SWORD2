using System.Globalization;
using System.Numerics;
using Sword.Bake.Engine.Anim;

namespace Sword.Bake.Engine;

/// <summary>
/// sword-bake inspect — диагностика исходников конвейера анимаций (команда плагина, см. <see cref="InspectCommand"/>).
///   meshinfo GLB                              сводка по glTF: вершины, суставы, границы, bind-точки ключевых костей
///   rawpose DIR --clip NAME [--frame N]       точки костей кадра сырого экспорта в пространстве персонажа
/// Сравнение паков — <c>sword-bake diff</c> ядра (Б3), не inspect.
/// </summary>
internal static class Inspect
{
    public static int Run(IReadOnlyList<string> args, TextWriter output)
    {
        if (args.Count == 0 || !Known.ContainsKey(args[0]))
            throw new ArgumentException($"{(args.Count == 0 ? "inspect: нужна команда" : $"inspect: неизвестная команда {args[0]}")}: {string.Join(" | ", Known.Keys)}");
        var opts = ParseOptions([.. args.Skip(1)], args[0]);
        switch (args[0])
        {
            case "meshinfo":
            {
                var mesh = GltfMesh.Load(Required(opts, ""));
                var inv = CultureInfo.InvariantCulture;
                output.WriteLine($"вершин {mesh.Positions.Length}, треугольников {mesh.Indices.Length / 3}, суставов {mesh.JointNames.Length}");
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                foreach (var p in mesh.Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
                output.WriteLine(string.Create(inv, $"границы ({min.X:F3},{min.Y:F3},{min.Z:F3}) … ({max.X:F3},{max.Y:F3},{max.Z:F3})"));
                output.WriteLine("суставы: " + string.Join(", ", mesh.JointNames.Take(16)));
                foreach (var name in new[] { "root", "pelvis", "head", "hand_r", "hand_l", "foot_l", "weapon_r" })
                {
                    int j = Array.IndexOf(mesh.JointNames, name);
                    if (j < 0) continue;
                    Matrix4x4.Invert(mesh.InverseBind[j], out var bind);
                    output.WriteLine(string.Create(inv, $"  {name,-9} bind t=({bind.M41:F3},{bind.M42:F3},{bind.M43:F3})"));
                }
                break;
            }
            case "rawpose":
                PoseReport.Raw(Required(opts, ""), Required(opts, "--clip"), Frame(opts), output);
                break;
        }
        return 0;
    }

    private static int Frame(Dictionary<string, string> o) =>
        o.TryGetValue("--frame", out var fr) ? int.Parse(fr, CultureInfo.InvariantCulture) : 0;

    /// <summary>Команды и их ключи со значением; порядок — как в подсказке.</summary>
    private static readonly Dictionary<string, string[]> Known = new()
    {
        ["meshinfo"] = [],
        ["rawpose"] = ["--clip", "--frame"],
    };

    private static Dictionary<string, string> ParseOptions(ReadOnlySpan<string> args, string command)
    {
        var known = Known[command];
        var d = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (known.Contains(a) && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) d[a] = args[++i];
            else if (!a.StartsWith("--", StringComparison.Ordinal) && !d.ContainsKey("")) d[""] = a;
            else throw new ArgumentException($"неизвестный или неполный аргумент {a}");
        }
        return d;
    }

    private static string Required(Dictionary<string, string> opts, string name) =>
        opts.TryGetValue(name, out var v) ? v : throw new ArgumentException($"нужен {(name.Length == 0 ? "файл или каталог" : name)}");
}

/// <summary>Команда плагина: <c>sword-bake inspect КОМАНДА …</c>.</summary>
internal sealed class InspectCommand : IBakeCommand
{
    public string Name => "inspect";
    public string Summary => "диагностика исходников анимаций: meshinfo (glTF), rawpose (кадр raw-экспорта)";

    public int Run(IReadOnlyList<string> args, TextWriter output) => Inspect.Run(args, output);
}
