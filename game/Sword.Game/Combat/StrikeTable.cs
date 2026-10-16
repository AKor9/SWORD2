using System.Globalization;
using System.Numerics;
using Sword.Engine.Animation;
using Sword.Game.Contracts;
using Sword.Game.Data;

namespace Sword.Game.Combat;

/// <summary>
/// Таблица ударов: slash1–4 (рубящие, связка) и stab1–3 (уколы) по клипам таблицы действий; окна урона — по траектории кончика
/// меча по умолчанию для всех мечей (<see cref="StrikeWindows.Find"/>; у укола — только самый быстрый удар). Неизменяемый ассет,
/// считается при загрузке ассетов бойца.
/// </summary>
public sealed class StrikeTable
{
    /// <summary>Слоты ударов по порядку: рубящие (<c>AttackSlots</c>), затем уколы (<c>StabSlots</c>).</summary>
    public static readonly string[] SlashSlots = ["slash1", "slash2", "slash3", "slash4"], StabSlots = ["stab1", "stab2", "stab3"];

    private readonly AttackInfo[] _attacks;

    private StrikeTable(AttackInfo[] attacks) => _attacks = attacks;

    /// <summary>Все удары: рубящие, затем уколы.</summary>
    public ReadOnlySpan<AttackInfo> Attacks => _attacks;
    public ReadOnlySpan<AttackInfo> Slashes => _attacks.AsSpan(0, SlashSlots.Length);
    public ReadOnlySpan<AttackInfo> Stabs => _attacks.AsSpan(SlashSlots.Length);

    /// <summary>Удар по клипу (<c>AttackOf</c>: первый с этим клипом среди рубящих, затем уколов); нет — null.</summary>
    public AttackInfo? Of(int clip)
    {
        foreach (var a in _attacks)
            if (a.Clip == clip) return a;
        return null;
    }

    /// <summary>
    /// Разметка ударов по клипам таблицы действий; кончик — клинок <paramref name="bladeTip"/> в системе сустава <paramref name="hand"/>.
    /// </summary>
    public static StrikeTable Analyse(ClipLibrary clips, ActionTable actions, int hand, Vector3 bladeTip)
    {
        var attacks = new List<AttackInfo>();
        foreach (string slot in SlashSlots) attacks.Add(Analyse(clips, actions, slot, false, hand, bladeTip));
        foreach (string slot in StabSlots) attacks.Add(Analyse(clips, actions, slot, true, hand, bladeTip));
        return new StrikeTable([.. attacks]);
    }

    private static AttackInfo Analyse(ClipLibrary clips, ActionTable actions, string slot, bool thrust, int hand, Vector3 bladeTip)
    {
        int s = ActionSlots.Index(slot);
        int clip = actions.Clip[s];
        var tips = Tips(clips, clip, hand, bladeTip);
        return new AttackInfo(slot, clip, clips.FindClip(clips.Clips[clip].Name + "_Blocked"), clips.Duration(clip), thrust,
            actions.Upper[s], StrikeWindows.Find(tips, clips.SampleRate, single: thrust));
    }

    /// <summary>
    /// Кончик клинка по кадрам клипа в пространстве персонажа: поза кадра <c>f / SampleRate</c>, прямая кинематика, кончик в системе кисти.
    /// </summary>
    public static Vector3[] Tips(ClipLibrary clips, int clip, int hand, Vector3 bladeTip)
    {
        var c = clips.Clips[clip];
        int jc = clips.JointCount;
        var rot = new Quaternion[jc];
        var pos = new Vector3[jc];
        var model = new Matrix4x4[jc];
        var tips = new Vector3[c.Count];
        for (int f = 0; f < c.Count; f++)
        {
            clips.Sample(clip, f / clips.SampleRate, rot, pos);
            clips.Skeleton.ToModel(rot, pos, model);
            tips[f] = Vector3.Transform(bladeTip, model[hand]);
        }
        return tips;
    }

    /// <summary>Разметка словами для журнала: клип, длительность, окна, пик и замах.</summary>
    public string Describe(ClipLibrary clips) => string.Join("; ", _attacks.Select(a => string.Create(CultureInfo.InvariantCulture,
        $"{a.Slot} {clips.Clips[a.Clip].ShortName}: {a.Duration:F2} с, ударов {a.Windows.Length}: ") + string.Join(", ",
        a.Windows.ToArray().Select(w => string.Create(CultureInfo.InvariantCulture,
            $"{w.Start:F2}–{w.End:F2} с {w.PeakSpeed:F1} м/с ({w.SwingDir.X:F1},{w.SwingDir.Y:F1})")))));
}
