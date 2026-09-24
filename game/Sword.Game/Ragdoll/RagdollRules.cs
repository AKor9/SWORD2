using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Ragdoll;

/// <summary>
/// Секция конфига <c>ragdoll</c> (7з): правила трупа на физике — ручки «Рэгдолл» (<see cref="RagdollKnobs"/>). Отправные значения — SWORD
/// 2eab13b (<c>RagdollSettings</c>, <c>DuelArena</c>, <c>KinematicContacts</c>, <c>Fighter.RagdollDynamics</c>, <c>TameDetached</c>) — источник
/// идей, не образец: подбираются на глаз ручками и замерами SWORD2.
/// </summary>
public sealed record RagdollSettings
{
    /// <summary>Трение тел трупа о землю μ (покоя и скольжения — <see cref="PhysicsSettings.StaticFriction"/>).</summary>
    public float Friction { get; init; } = 0.7f;

    /// <summary>Бюджет толчка живого, Н (<see cref="PhysicsSettings.PushForce"/>): сила, которой живой толкает труп.</summary>
    public float PushForce { get; init; } = 800f;

    /// <summary>
    /// Скорость выталкивания застарелой глубины, м/с (<see cref="PhysicsSettings.PopSpeed"/>; у физики и SWORD — 3). У трупа — 1: выход
    /// конечности из земли и трупов внахлёст идёт через суставы, и чем быстрее выход, тем сильнее он разгоняет весь труп (замер 7з: два трупа
    /// внахлёст — разошлись на 1,69 м при 3 м/с и на 1,12 м при 1 м/с, всплеск энергии за подшаг — 16 и 3,9 Дж; docs/history/ragdoll.md, «7з»).
    /// </summary>
    public float PopSpeed { get; init; } = 1f;

    /// <summary>Податливость пределов наклона-скручивания суставов трупа, рад/(Н·м) (<see cref="PhysicsSettings.JointLimitCompliance"/>).</summary>
    public float JointCompliance { get; init; } = 2e-4f;

    /// <summary>Потолки скоростей тел трупа (профиль трупа): м/с и рад/с.</summary>
    public float CorpseMaxSpeed { get; init; } = 15f;
    public float CorpseMaxAngularSpeed { get; init; } = 40f;

    /// <summary>Ограничение скоростей тел при переходе в труп (скорости последней постановки живого): м/с и рад/с.</summary>
    public float DeathMaxSpeed { get; init; } = 6f;
    public float DeathMaxAngularSpeed { get; init; } = 20f;

    /// <summary>
    /// Доля отдачи в скорость корня живого (7к; <c>EnemyReact</c>, <c>PlayerReact</c> SWORD — отправные): горизонталь отдачи тика (толчки
    /// трупов и удары по ним) × доля / масса бойца. Игроку — четверть: отзывчивость (игрок не теряет управления).
    /// </summary>
    public float EnemyRecoil { get; init; } = 1f;
    public float PlayerRecoil { get; init; } = 0.25f;

    /// <summary>
    /// Предел отдачи, м/с (7к): отдача не уводит скорость корня от заданной (намерение × масштаб хода действия) вдоль себя дальше этого —
    /// сквозь кучу трупов боец идёт, теряя не больше предела; дальше скорость возвращает разгон бойца (14 1/с).
    /// </summary>
    public float RecoilMaxSpeed { get; init; } = 1f;

    /// <summary>
    /// Скорость сведения разрыва сустава, м/с (7з-2, ручка — 7з-3; <see cref="PhysicsSettings.JointPopSpeed"/>): унаследованный разрыв точки
    /// сустава до 1 см (удар трупа о землю) сводится не быстрее её — всплеск энергии при ударе меньше.
    /// </summary>
    public float JointPopSpeed { get; init; } = 1.5f;

    /// <summary>Трение между трупами μ (7з-2, ручка — 7з-3; <see cref="PhysicsSettings.PairFriction"/>): труп, лёгший на другой, не съезжает с него.</summary>
    public float PairFriction { get; init; } = 0.7f;

    /// <summary>
    /// Порог сна трупа, м/с (7з-3; <see cref="PhysicsSettings.SleepSpeed"/>): труп целиком (среднеквадратичная скорость его массы) медленнее —
    /// может уснуть. 0 — сна нет.
    /// </summary>
    public float SleepSpeed { get; init; } = 0.05f;

    /// <summary>
    /// Порог сна части, м/с (7з-3; <see cref="PhysicsSettings.SleepBodySpeed"/>): каждая часть трупа медленнее — может уснуть; живой или труп,
    /// который у спящего быстрее, будит его. 0 — сна нет.
    /// </summary>
    public float SleepBodySpeed { get; init; } = 0.5f;

    /// <summary>Время покоя до сна, с (7з-3; <see cref="PhysicsSettings.SleepTime"/>).</summary>
    public float SleepTime { get; init; } = 0.5f;
}

/// <summary>
/// Правила трупа на физике — ресурс-значение мира (7з; хеширует участник <c>ecs</c>) из секции конфига <c>ragdoll</c>, ручки «Рэгдолл»
/// меняют его командой ручки. Настройки физики (<see cref="PhysicsSettings"/>) и профиль динамики трупа из него производны
/// (<see cref="Physics"/>, <see cref="Corpse"/>): их ставит сборка (<see cref="GameCore"/>) и замена правил
/// (<see cref="RagdollRulesChangeSystem"/>) — в хеш они сами не входят, входят правила.
/// </summary>
public struct RagdollRules
{
    /// <summary>Трение о землю μ.</summary>
    public float Friction;

    /// <summary>Бюджет толчка живого, Н.</summary>
    public float PushForce;

    /// <summary>Скорость выталкивания застарелой глубины, м/с.</summary>
    public float PopSpeed;

    /// <summary>Податливость пределов наклона-скручивания, рад/(Н·м).</summary>
    public float JointCompliance;

    /// <summary>Потолки скоростей тел трупа, м/с и рад/с.</summary>
    public float CorpseMaxSpeed, CorpseMaxAngularSpeed;

    /// <summary>Ограничение скоростей при переходе в труп, м/с и рад/с.</summary>
    public float DeathMaxSpeed, DeathMaxAngularSpeed;

    /// <summary>Доля отдачи в скорость корня врага и игрока (7к).</summary>
    public float EnemyRecoil, PlayerRecoil;

    /// <summary>Предел отдачи — наибольшее отклонение скорости корня от заданной вдоль отдачи, м/с (7к).</summary>
    public float RecoilMaxSpeed;

    /// <summary>Скорость сведения разрыва сустава, м/с, и трение между трупами μ (7з-2; ручки — 7з-3).</summary>
    public float JointPopSpeed, PairFriction;

    /// <summary>Сон трупа (7з-3): порог трупа целиком и части, м/с, время покоя до сна, с.</summary>
    public float SleepSpeed, SleepBodySpeed, SleepTime;

    /// <summary>Правила из секции конфига; числа вне пределов — ошибка конфига.</summary>
    public static RagdollRules From(RagdollSettings s)
    {
        static float Check(float value, string key, bool positive)
        {
            if (!float.IsFinite(value) || value < 0 || positive && value == 0)
                throw new InvalidDataException($"{RagdollKnobs.Section}.{key} = {value}: нужно конечное {(positive ? "больше 0" : "не меньше 0")}");
            return value;
        }
        return new RagdollRules
        {
            Friction = Check(s.Friction, "friction", false),
            PushForce = Check(s.PushForce, "pushForce", false),
            PopSpeed = Check(s.PopSpeed, "popSpeed", false),
            JointCompliance = Check(s.JointCompliance, "jointCompliance", false),
            CorpseMaxSpeed = Check(s.CorpseMaxSpeed, "corpseMaxSpeed", true),
            CorpseMaxAngularSpeed = Check(s.CorpseMaxAngularSpeed, "corpseMaxAngularSpeed", true),
            DeathMaxSpeed = Check(s.DeathMaxSpeed, "deathMaxSpeed", false),
            DeathMaxAngularSpeed = Check(s.DeathMaxAngularSpeed, "deathMaxAngularSpeed", false),
            EnemyRecoil = Check(s.EnemyRecoil, "enemyRecoil", false),
            PlayerRecoil = Check(s.PlayerRecoil, "playerRecoil", false),
            RecoilMaxSpeed = Check(s.RecoilMaxSpeed, "recoilMaxSpeed", false),
            JointPopSpeed = Check(s.JointPopSpeed, "jointPopSpeed", true),
            PairFriction = Check(s.PairFriction, "pairFriction", false),
            SleepSpeed = Check(s.SleepSpeed, "sleepSpeed", false),
            SleepBodySpeed = Check(s.SleepBodySpeed, "sleepBodySpeed", false),
            SleepTime = Check(s.SleepTime, "sleepTime", true),
        };
    }

    /// <summary>
    /// Настройки физики по правилам: трение о землю и между трупами, бюджет толчка, выталкивание, податливость пределов, сведение разрыва
    /// сустава, сон (7з-3); прочее — из <paramref name="baseline"/>.
    /// </summary>
    public readonly PhysicsSettings Physics(PhysicsSettings baseline) => baseline with
    {
        StaticFriction = Friction, PushForce = PushForce, PopSpeed = PopSpeed, JointLimitCompliance = JointCompliance,
        JointPopSpeed = JointPopSpeed, PairFriction = PairFriction, SleepSpeed = SleepSpeed, SleepBodySpeed = SleepBodySpeed, SleepTime = SleepTime,
    };

    /// <summary>Профиль динамики трупа: потолки скоростей трупа, остальное — профиль по умолчанию (гравитация, демпфирование, трение).</summary>
    public readonly BodyDynamics Corpse() => new BodyDynamics() with { MaxSpeed = CorpseMaxSpeed, MaxAngularSpeed = CorpseMaxAngularSpeed };
}

/// <summary>
/// Ручки «Рэгдолл» (7з; механизм 6е — как «Бой»; 7к — отдача: доли врагу и игроку, предел; 7з-3 — сведение разрыва сустава и трение между
/// трупами 7з-2, сон трупа: пороги трупа и части, время): поля <see cref="RagdollRules"/> — ресурса мира из секции конфига <c>ragdoll</c>.
/// Изменение на ходу — команда ручки (система <c>tuning.ragdoll</c> в Begin), в реплее; замену настроек физики и профиля трупа делает
/// <see cref="RagdollRulesChangeSystem"/> (Intent) — до шага физики того же тика. Группа F3 «Рэгдолл» — клиент (7и).
/// </summary>
public static class RagdollKnobs
{
    public const string Section = "ragdoll";

    /// <summary>Полные имена ручек (ключи конфига).</summary>
    public const string Friction = "ragdoll.friction", PushForce = "ragdoll.pushForce", PopSpeed = "ragdoll.popSpeed",
        JointCompliance = "ragdoll.jointCompliance", CorpseMaxSpeed = "ragdoll.corpseMaxSpeed", CorpseMaxAngularSpeed = "ragdoll.corpseMaxAngularSpeed",
        DeathMaxSpeed = "ragdoll.deathMaxSpeed", DeathMaxAngularSpeed = "ragdoll.deathMaxAngularSpeed", EnemyRecoil = "ragdoll.enemyRecoil",
        PlayerRecoil = "ragdoll.playerRecoil", RecoilMaxSpeed = "ragdoll.recoilMaxSpeed", JointPopSpeed = "ragdoll.jointPopSpeed",
        PairFriction = "ragdoll.pairFriction", SleepSpeed = "ragdoll.sleepSpeed", SleepBodySpeed = "ragdoll.sleepBodySpeed",
        SleepTime = "ragdoll.sleepTime";

    public static void Register(SimulationBuilder b, IPhysicsWorld physics, int corpseProfile)
    {
        var d = new RagdollSettings();
        b.Tuning<RagdollRules>(Section)
            .Float("friction", "трение о землю", (in RagdollRules r) => r.Friction, (ref RagdollRules r, float v) => r.Friction = v,
                0, 2, d.Friction, format: "0.00", hint: "μ трения тел трупа о землю (покоя и скольжения): больше — труп меньше скользит по склону и ступеням")
            .Float("pushForce", "толчок живого", (in RagdollRules r) => r.PushForce, (ref RagdollRules r, float v) => r.PushForce = v,
                0, 4000, d.PushForce, format: "0", hint: "наибольшая сила, Н, которой живой толкает один труп (все тела вместе)")
            .Float("popSpeed", "выталкивание", (in RagdollRules r) => r.PopSpeed, (ref RagdollRules r, float v) => r.PopSpeed = v,
                0.1f, 10, d.PopSpeed, log: true, format: "0.00",
                hint: "м/с: так быстро уходит застарелая глубина — конечность в земле в позе смерти, трупы внахлёст")
            .Float("jointCompliance", "мягкость суставов", (in RagdollRules r) => r.JointCompliance, (ref RagdollRules r, float v) => r.JointCompliance = v,
                1e-6f, 1e-2f, d.JointCompliance, log: true, format: "0.000000",
                hint: "податливость пределов наклона и скручивания, рад/(Н·м): больше — суставы трупа мягче заходят за предел")
            .Float("corpseMaxSpeed", "потолок скорости трупа", (in RagdollRules r) => r.CorpseMaxSpeed, (ref RagdollRules r, float v) => r.CorpseMaxSpeed = v,
                1, 40, d.CorpseMaxSpeed, format: "0.0", hint: "м/с: быстрее тело трупа не движется (страховка от взрыва решателя)")
            .Float("corpseMaxAngularSpeed", "потолок вращения трупа", (in RagdollRules r) => r.CorpseMaxAngularSpeed,
                (ref RagdollRules r, float v) => r.CorpseMaxAngularSpeed = v, 1, 60, d.CorpseMaxAngularSpeed, format: "0",
                hint: "рад/с: быстрее тело трупа не вращается")
            .Float("deathMaxSpeed", "скорость при смерти", (in RagdollRules r) => r.DeathMaxSpeed, (ref RagdollRules r, float v) => r.DeathMaxSpeed = v,
                0, 20, d.DeathMaxSpeed, format: "0.0", hint: "м/с: скорость тел живого (разность постановок) при переходе в труп урезается до неё")
            .Float("deathMaxAngularSpeed", "вращение при смерти", (in RagdollRules r) => r.DeathMaxAngularSpeed,
                (ref RagdollRules r, float v) => r.DeathMaxAngularSpeed = v, 0, 60, d.DeathMaxAngularSpeed, format: "0",
                hint: "рад/с: угловая скорость тел при переходе в труп (ложная закрутка кинематических капсул) урезается до неё")
            .Float("enemyRecoil", "отдача врагу", (in RagdollRules r) => r.EnemyRecoil, (ref RagdollRules r, float v) => r.EnemyRecoil = v,
                0, 4, d.EnemyRecoil, format: "0.00",
                hint: "доля отдачи толчков и ударов по трупам в скорость корня врага (импульс / масса бойца × доля)")
            .Float("playerRecoil", "отдача игроку", (in RagdollRules r) => r.PlayerRecoil, (ref RagdollRules r, float v) => r.PlayerRecoil = v,
                0, 4, d.PlayerRecoil, format: "0.00",
                hint: "доля отдачи толчков и ударов по трупам в скорость корня игрока: меньше — игрок увереннее идёт сквозь трупы")
            .Float("recoilMaxSpeed", "предел отдачи", (in RagdollRules r) => r.RecoilMaxSpeed, (ref RagdollRules r, float v) => r.RecoilMaxSpeed = v,
                0, 10, d.RecoilMaxSpeed, format: "0.00",
                hint: "м/с: дальше этого отдача не уводит скорость корня от заданной — сквозь кучу трупов боец не останавливается")
            .Float("jointPopSpeed", "сведение разрыва сустава", (in RagdollRules r) => r.JointPopSpeed, (ref RagdollRules r, float v) => r.JointPopSpeed = v,
                0.1f, 10, d.JointPopSpeed, log: true, format: "0.00",
                hint: "м/с: так быстро сводится разрыв сустава после удара трупа о землю; больше — резче, всплеск энергии при ударе сильнее")
            .Float("pairFriction", "трение между трупами", (in RagdollRules r) => r.PairFriction, (ref RagdollRules r, float v) => r.PairFriction = v,
                0, 2, d.PairFriction, format: "0.00", hint: "μ трения трупа о труп (покоя и скольжения): меньше — лёгший на другой труп съезжает с него")
            .Float("sleepSpeed", "порог сна трупа", (in RagdollRules r) => r.SleepSpeed, (ref RagdollRules r, float v) => r.SleepSpeed = v,
                0, 0.5f, d.SleepSpeed, format: "0.000",
                hint: "м/с: труп целиком (среднеквадратичная скорость его массы) медленнее этого «время до сна» — засыпает; 0 — сна нет")
            .Float("sleepBodySpeed", "порог сна части", (in RagdollRules r) => r.SleepBodySpeed, (ref RagdollRules r, float v) => r.SleepBodySpeed = v,
                0, 2, d.SleepBodySpeed, format: "0.00",
                hint: "м/с: каждая часть трупа медленнее этого — может уснуть; живой или труп, который у спящего быстрее, будит его; 0 — сна нет")
            .Float("sleepTime", "время до сна", (in RagdollRules r) => r.SleepTime, (ref RagdollRules r, float v) => r.SleepTime = v,
                0.05f, 10, d.SleepTime, log: true, format: "0.00",
                hint: "с: столько подряд труп медленнее порогов — и засыпает (физика его не считает, крест капсулы — синий, F7)");
        b.System(GameStages.Intent, new RagdollRulesChangeSystem(physics, corpseProfile));
    }
}

/// <summary>
/// Intent, первой (<see cref="GameLabels.RagdollRules"/>; 7з), реактивная по <c>ResourceChanged&lt;RagdollRules&gt;</c>: ручки «Рэгдолл»
/// сменили правила (система ручек в Begin этого тика) — настройки физики заменяются целиком (<see cref="IPhysicsWorld.Settings"/>, от
/// нынешних: число подшагов и доли толчков — прежние) и профиль трупа (<see cref="IPhysicsWorld.ReplaceDynamics"/>): шаг этого тика идёт
/// уже по новым. Мир ECS не меняет. 7з-3: изменившиеся настройки физики и профиль трупа будят спящие трупы (физика); ручки, которые физику не
/// меняют (отдача живому), — не будят.
/// </summary>
public sealed class RagdollRulesChangeSystem(IPhysicsWorld physics, int corpseProfile) : IReactiveSystem<ResourceChanged<RagdollRules>>
{
    public void Setup(SystemSetup s) => s.Label(GameLabels.RagdollRules).Before(GameLabels.SceneCommand);

    public void React(in SystemContext ctx, ReadOnlySpan<ResourceChanged<RagdollRules>> changes) =>
        Apply(physics, corpseProfile, changes[^1].Value);

    /// <summary>Правила — в физику: настройки и профиль трупа.</summary>
    public static void Apply(IPhysicsWorld physics, int corpseProfile, in RagdollRules rules)
    {
        physics.Settings = rules.Physics(physics.Settings);
        physics.ReplaceDynamics(corpseProfile, rules.Corpse());
    }
}
