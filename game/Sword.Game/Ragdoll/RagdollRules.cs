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
    /// внахлёст — разошлись на 1,69 м при 3 м/с и на 1,12 м при 1 м/с, всплеск энергии за подшаг — 16 и 3,9 Дж; README «Рэгдолл», «7з»).
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
        };
    }

    /// <summary>Настройки физики по правилам: трение, бюджет толчка, выталкивание, податливость пределов; прочее — из <paramref name="baseline"/>.</summary>
    public readonly PhysicsSettings Physics(PhysicsSettings baseline) => baseline with
    {
        StaticFriction = Friction, PushForce = PushForce, PopSpeed = PopSpeed, JointLimitCompliance = JointCompliance,
    };

    /// <summary>Профиль динамики трупа: потолки скоростей трупа, остальное — профиль по умолчанию (гравитация, демпфирование, трение).</summary>
    public readonly BodyDynamics Corpse() => new BodyDynamics() with { MaxSpeed = CorpseMaxSpeed, MaxAngularSpeed = CorpseMaxAngularSpeed };
}

/// <summary>
/// Ручки «Рэгдолл» (7з; механизм 6е — как «Бой»): поля <see cref="RagdollRules"/> — ресурса мира из секции конфига <c>ragdoll</c>.
/// Изменение на ходу — команда ручки (система <c>tuning.ragdoll</c> в Begin), в реплее; замену настроек физики и профиля трупа делает
/// <see cref="RagdollRulesChangeSystem"/> (Intent) — до шага физики того же тика. Группа F3 «Рэгдолл» — клиент (7и).
/// </summary>
public static class RagdollKnobs
{
    public const string Section = "ragdoll";

    /// <summary>Полные имена ручек (ключи конфига).</summary>
    public const string Friction = "ragdoll.friction", PushForce = "ragdoll.pushForce", PopSpeed = "ragdoll.popSpeed",
        JointCompliance = "ragdoll.jointCompliance", CorpseMaxSpeed = "ragdoll.corpseMaxSpeed", CorpseMaxAngularSpeed = "ragdoll.corpseMaxAngularSpeed",
        DeathMaxSpeed = "ragdoll.deathMaxSpeed", DeathMaxAngularSpeed = "ragdoll.deathMaxAngularSpeed";

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
                hint: "рад/с: угловая скорость тел при переходе в труп (ложная закрутка кинематических капсул) урезается до неё");
        b.System(GameStages.Intent, new RagdollRulesChangeSystem(physics, corpseProfile));
    }
}

/// <summary>
/// Intent, первой (<see cref="GameLabels.RagdollRules"/>; 7з), реактивная по <c>ResourceChanged&lt;RagdollRules&gt;</c>: ручки «Рэгдолл»
/// сменили правила (система ручек в Begin этого тика) — настройки физики заменяются целиком (<see cref="IPhysicsWorld.Settings"/>, от
/// нынешних: число подшагов и доли толчков — прежние) и профиль трупа (<see cref="IPhysicsWorld.ReplaceDynamics"/>): шаг этого тика идёт
/// уже по новым. Мир ECS не меняет.
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
