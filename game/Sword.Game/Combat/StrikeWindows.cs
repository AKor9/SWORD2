using System.Numerics;
using Sword.Game.Contracts;

namespace Sword.Game.Combat;

/// <summary>
/// Разметка ударов по скорости кончика клинка — <c>StrikeWindows</c> SWORD 2eab13b, чистая функция (перенос: константы,
/// порядок вычислений float и порядок перебора те же). Клип
/// может быть связкой из нескольких ударов, поэтому ищется не один пик, а все локальные максимумы скорости выше порога; окно
/// удара — кадры вокруг пика, пока скорость выше доли пика. Соседние пики без заметного провала скорости между ними — один
/// взмах (сливаются), короткие всплески отбрасываются. Зовётся при загрузке ассетов (выделения — не в тике).
/// </summary>
public static class StrikeWindows
{
    /// <summary>Пик ниже этой доли самого быстрого удара клипа — перехват или замах, не удар.</summary>
    public const float PeakShare = 0.35f;

    /// <summary>Пик медленнее (м/с) — не удар при любой доле; ниже самого медленного укола пака (~9 м/с).</summary>
    public const float MinPeakSpeed = 6f;

    /// <summary>Окно удара — кадры вокруг пика со скоростью выше этой доли пика.</summary>
    public const float WindowShare = 0.5f;

    /// <summary>Провал скорости между пиками выше этой доли меньшего пика — это один взмах.</summary>
    public const float ValleyShare = 0.6f;

    /// <summary>Окно короче (кадров) — всплеск, а не удар.</summary>
    public const int MinFrames = 2;

    /// <summary>
    /// Возврат клинка после удара: движение против предыдущего (косинус ниже этого) и медленнее его в
    /// <see cref="ReturnShare"/> раз — не удар. Обратный удар связки (форхенд → бэкхенд) быстрее и остаётся.
    /// </summary>
    public const float ReturnCos = -0.3f, ReturnShare = 0.75f;

    /// <summary>
    /// Замах перед ударом: против следующего, более быстрого удара (<see cref="ReturnShare"/>) вплотную к нему (разрыв не
    /// больше <see cref="WindupGap"/>, с) — или медленнее его в <see cref="LeadShare"/> раз при разрыве до <see cref="LeadGap"/> с
    /// в любом направлении (подъём клинка из стойки).
    /// </summary>
    public const float WindupGap = 0.1f, LeadShare = 0.5f, LeadGap = 0.6f;

    /// <summary>Окно, начатое в первых кадрах (клинок уже летит на входе в клип), при более быстром ударе дальше — не удар.</summary>
    public const int EntryFrames = 2;

    /// <param name="tips">Кончик клинка по кадрам в пространстве персонажа (+X — влево, Y — вверх).</param>
    /// <param name="sampleRate">Кадров клипа в секунду.</param>
    /// <param name="single">Только самый быстрый удар (укол: выдёргивание клинка так же быстро, как выпад).</param>
    /// <returns>Окна по времени; всегда хотя бы одно — у самого быстрого кадра.</returns>
    public static StrikeWindow[] Find(ReadOnlySpan<Vector3> tips, float sampleRate, bool single = false)
    {
        int n = tips.Length;
        if (n < 2) return [new StrikeWindow(0, 0, 0, 0, Vector2.UnitY)];
        var raw = new float[n];
        for (int f = 1; f < n; f++) raw[f] = Vector3.Distance(tips[f], tips[f - 1]) * sampleRate;
        // сглаживание по трём кадрам: одиночный рывок сэмплирования не должен давать отдельный пик
        var speed = new float[n];
        for (int f = 1; f < n; f++)
        {
            float prev = f > 1 ? raw[f - 1] : raw[f], next = f + 1 < n ? raw[f + 1] : raw[f];
            speed[f] = (prev + 2 * raw[f] + next) * 0.25f;
        }
        int top = 1;
        for (int f = 2; f < n; f++) if (speed[f] > speed[top]) top = f;
        float threshold = MathF.Max(MinPeakSpeed, speed[top] * PeakShare);

        // окна от сильных пиков к слабым (при равной скорости — раньше по кадру: сортировка устойчивая, как OrderByDescending
        // SWORD); пик внутри уже занятого окна — часть того же удара
        var peaks = new List<int>();
        for (int f = 1; f < n; f++)
            if (speed[f] >= threshold && speed[f] >= speed[f - 1] && (f + 1 >= n || speed[f] >= speed[f + 1])) peaks.Add(f);
        var ordered = peaks.OrderByDescending(f => speed[f]).ToArray();
        var owner = new int[n];
        Array.Fill(owner, -1);
        var spans = new List<(int A, int B, int Peak)>();
        foreach (int p in ordered)
        {
            if (owner[p] >= 0) continue;
            int a = p, b = p;
            while (a > 1 && owner[a - 1] < 0 && speed[a - 1] > speed[p] * WindowShare) a--;
            while (b + 1 < n && owner[b + 1] < 0 && speed[b + 1] > speed[p] * WindowShare) b++;
            for (int f = a; f <= b; f++) owner[f] = spans.Count;
            spans.Add((a, b, p));
        }
        spans.Sort((x, y) => x.A.CompareTo(y.A));   // начала окон не совпадают: окна не пересекаются

        // слияние соседей без провала скорости между пиками
        var merged = new List<(int A, int B, int Peak)>();
        foreach (var s in spans)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                float valley = float.MaxValue;
                for (int f = last.Peak; f <= s.Peak; f++) valley = MathF.Min(valley, speed[f]);
                if (valley > ValleyShare * MathF.Min(speed[last.Peak], speed[s.Peak]))
                {
                    merged[^1] = (last.A, s.B, speed[s.Peak] > speed[last.Peak] ? s.Peak : last.Peak);
                    continue;
                }
            }
            merged.Add(s);
        }
        merged.RemoveAll(s => s.B - s.A + 1 < MinFrames && s.Peak != top);
        var points = tips.ToArray();   // лямбды не держат span
        Vector3 Move((int A, int B, int Peak) s) => points[s.B] - points[Math.Max(s.A - 1, 0)];
        for (int k = 1; k < merged.Count; k++)
        {
            Vector3 prev = Move(merged[k - 1]), cur = Move(merged[k]);
            float lengths = prev.Length() * cur.Length();
            if (lengths > 1e-8f && Vector3.Dot(prev, cur) / lengths < ReturnCos
                && speed[merged[k].Peak] < ReturnShare * speed[merged[k - 1].Peak])
                merged.RemoveAt(k--);
        }
        for (int k = merged.Count - 2; k >= 0; k--)
        {
            Vector3 cur = Move(merged[k]), next = Move(merged[k + 1]);
            float lengths = cur.Length() * next.Length();
            float gap = (merged[k + 1].A - merged[k].B) / sampleRate, ratio = speed[merged[k].Peak] / speed[merged[k + 1].Peak];
            bool reversed = lengths > 1e-8f && Vector3.Dot(cur, next) / lengths < ReturnCos;
            if (reversed && ratio < ReturnShare && gap <= WindupGap || ratio < LeadShare && gap <= LeadGap)
                merged.RemoveAt(k);
        }
        if (merged.Count > 1 && merged[0].A <= EntryFrames && merged.Skip(1).Any(s => speed[s.Peak] > speed[merged[0].Peak]))
            merged.RemoveAt(0);
        if (single) merged.RemoveAll(s => owner[top] >= 0 && !(s.A <= top && top <= s.B));
        if (merged.Count == 0) merged.Add((top, top, top));

        var windows = new StrikeWindow[merged.Count];
        for (int k = 0; k < merged.Count; k++)
        {
            var s = merged[k];
            var move = Move(s);
            var dir = new Vector2(-move.X, move.Y);   // +X персонажа — лево
            windows[k] = new StrikeWindow((s.A - 1) / sampleRate, s.B / sampleRate, s.Peak / sampleRate, speed[s.Peak],
                dir.LengthSquared() > 1e-6f ? Vector2.Normalize(dir) : Vector2.UnitY);
        }
        return windows;
    }
}
