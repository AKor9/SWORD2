using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Game.Ragdoll;

namespace Sword.Game.Client;

/// <summary>
/// Вкладка «Рэгдолл» панели ручек F3 (7и, часть 2; механизм 6е — как <see cref="CombatTuning"/>): все ручки симуляции секции
/// <c>ragdoll</c> (<see cref="RagdollKnobs"/>: восемь 7з, три отдачи живому 7к, сведение разрыва сустава и трение между трупами 7з-2, три сна
/// 7з-3, трение суставов и самоколлизия 7з-4; подписи, подсказки, пределы и значения по умолчанию — из их регистрации) через привязку
/// клиента (<see cref="TuningBinding"/>: значение — ожидающее или мира, изменение — команда ручки на ближайший тик, в реплее; замену настроек
/// физики и профиля трупа делает симуляция в том же тике) и строки трупов кадра (<see cref="ArenaView.CorpseStats"/>): сколько трупов и в
/// покое, тел на физике и на земле, быстрейшее тело — видно, как ручка меняет падение и покой. Ключи группы — ключи секции
/// (<c>friction</c>): «сохранить» и «загрузить» пишут и читают секцию <c>ragdoll</c> файла <c>sword.user.json</c>. Строки состояния пишут
/// текст на стек — кадр панели без выделений.
/// </summary>
public static class RagdollTuning
{
    public const string Id = RagdollKnobs.Section, Title = "Рэгдолл";

    /// <summary>Группа «Рэгдолл» над привязкой <paramref name="tuning"/>; трупы — мира представления <paramref name="view"/> (последний кадр).</summary>
    public static TuningGroup Build(TuningBinding tuning, ArenaView view)
    {
        var g = new TuningGroup(Id, Title);
        g.Header("ТРУПЫ В КАДРЕ");
        g.Info("трупов", d => Corpses(d, view.CorpseStats),
            "мёртвых бойцов, нарисованных по телам; в покое — скорость центра масс тел ≤ 0,05 м/с; спят — физика их не считает (F7: синий крест)");
        g.Info("тел на физике", d => Bodies(d, view.CorpseStats),
            "тел трупов на физике; на земле — касаются земли арены (F7: крест в центре капсулы зелёный)");
        g.Info("быстрейшее тело", d => Fastest(d, view.CorpseStats),
            "наибольшие скорость и вращение тел трупов на тике; упор в потолок — число равно ручке «потолок…»");
        g.Header("ТРУП НА ЗЕМЛЕ");
        tuning.Add(g, RagdollKnobs.Friction);
        tuning.Add(g, RagdollKnobs.PairFriction);
        tuning.Add(g, RagdollKnobs.PopSpeed);
        tuning.Add(g, RagdollKnobs.JointCompliance);
        tuning.Add(g, RagdollKnobs.JointFriction);
        tuning.Add(g, RagdollKnobs.SelfCollision);
        tuning.Add(g, RagdollKnobs.JointPopSpeed);
        g.Header("СОН ТРУПА");
        tuning.Add(g, RagdollKnobs.SleepSpeed);
        tuning.Add(g, RagdollKnobs.SleepBodySpeed);
        tuning.Add(g, RagdollKnobs.SleepTime);
        g.Header("ПОТОЛКИ ТРУПА");
        tuning.Add(g, RagdollKnobs.CorpseMaxSpeed);
        tuning.Add(g, RagdollKnobs.CorpseMaxAngularSpeed);
        g.Header("ПЕРЕХОД В ТРУП");
        tuning.Add(g, RagdollKnobs.DeathMaxSpeed);
        tuning.Add(g, RagdollKnobs.DeathMaxAngularSpeed);
        g.Header("ТОЛЧОК ЖИВОГО");
        tuning.Add(g, RagdollKnobs.PushForce);
        tuning.Add(g, RagdollKnobs.EnemyRecoil);
        tuning.Add(g, RagdollKnobs.PlayerRecoil);
        tuning.Add(g, RagdollKnobs.RecoilMaxSpeed);
        return g;
    }

    /// <summary>«3, в покое 2, спят 1» — трупов кадра, из них в покое и спящих (7з-3); трупов нет — «0».</summary>
    public static int Corpses(Span<char> d, in CorpseFrameStats s) =>
        s.Corpses == 0 ? Length(new Line(d).S("0").Text)
            : Length(new Line(d).I(s.Corpses).S(", в покое ").I(s.AtRest).S(", спят ").I(s.Asleep).Text);

    /// <summary>«42, на земле 17» — тел трупов на физике и из них на земле; нет — «0».</summary>
    public static int Bodies(Span<char> d, in CorpseFrameStats s) =>
        s.SimulatedBodies == 0 ? Length(new Line(d).S("0").Text)
            : Length(new Line(d).I(s.SimulatedBodies).S(", на земле ").I(s.GroundedBodies).Text);

    /// <summary>«0.42 м/с, 1.3 рад/с» — наибольшие скорость и вращение тела трупа на физике; тел на физике нет — «—».</summary>
    public static int Fastest(Span<char> d, in CorpseFrameStats s) =>
        s.SimulatedBodies == 0 ? Length(new Line(d).S("—").Text)
            : Length(new Line(d).F(s.MaxSpeed, "F2").S(" м/с, ").F(s.MaxAngularSpeed, "F1").S(" рад/с").Text);

    /// <summary>Строка уже на месте (<see cref="Line"/> пишет в тот же буфер) — её длина.</summary>
    private static int Length(ReadOnlySpan<char> text) => text.Length;
}
