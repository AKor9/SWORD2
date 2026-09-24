using System.Globalization;
using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Arena;
using Sword.Game.Contracts;

namespace Sword.Game.Movement;

/// <summary>
/// <c>movement.rootOverlap</c> — после стадии Movement корни живых бойцов не остаются перекрытыми (круги по 0,35 м).
/// Два прохода <c>ResolveRoots</c> SWORD не разводят толпу до конца: пара, разведённая в проходе, снова сближается
/// толчком следующей пары; а спаун ставит новых врагов кучкой, в том числе прямо в толпу (у края арены кольцо спауна
/// прижато к окружности <c>ArenaRadius − 3</c>, где толпа обступила игрока). Такие перекрытия рассасываются за несколько
/// тиков, поэтому оба правила — на длительность, без особой логики вокруг спауна; пороги — по замеру с запасом 2 раза
/// (фича «Враги», «Инвариант перекрытия: замер»):
/// <list type="bullet">
/// <item>корни ближе <see cref="HardLimit"/> (радиус корня: корень бойца в круге соседа) — не дольше
/// <see cref="HardMaxTicks"/> тиков подряд. Худшее в честной игре — 2 тика (у края, −32 и +32 врага разом);</item>
/// <item>перекрытие больше <see cref="Tolerance"/> — не дольше <see cref="MaxTicks"/> тиков подряд. Худшее — 12 тиков (у
/// края, те же команды); в толпе вдали от края — 1–2 тика, установившееся перекрытие толпы — не больше 0,005 м.</item>
/// </list>
/// Пара — по местам <see cref="FighterSlot"/>; пара, где у бойца места нет (нарушение <c>arena.slots</c>), проверяется
/// без счёта — сразу. Совпавшие корни (ближе 1e-4 м) SWORD не разводит вовсе: если их не растолкают соседи, сработает
/// первое правило. Мёртвые (6г) исключены, как в <see cref="RootCollision.Resolve"/>: живые проходят сквозь замерший труп. Счёт тиков
/// подряд ведут плановые проверки после Movement: проверка по запросу (<c>invariant.run</c>) и повтор того же тика его не
/// сдвигают, пропуск тиков (инвариант выключали) начинает заново. Без нарушений не выделяет памяти.
/// </summary>
public sealed class RootOverlapInvariant : IInvariant
{
    /// <summary>Ближе этого, м, — корень в круге соседа (радиус корня).</summary>
    public const float HardLimit = MovementRules.RootRadius;

    /// <summary>Сколько тиков подряд корни могут быть ближе <see cref="HardLimit"/> (худший замер — 2).</summary>
    public const int HardMaxTicks = 4;

    /// <summary>Перекрытие, которое ещё не считается, м (установившееся перекрытие толпы — не больше 0,005 м).</summary>
    public const float Tolerance = 0.02f;

    /// <summary>Сколько тиков подряд перекрытие может быть больше <see cref="Tolerance"/> (худший замер — 12).</summary>
    public const int MaxTicks = 24;

    private const int Slots = ArenaRules.MaxFighters;
    private const float Diameter = 2 * MovementRules.RootRadius;

    private ReadQuery? _fighters;
    private Vector3[] _roots = new Vector3[Slots];
    private Entity[] _entities = new Entity[Slots];
    private int[] _slots = new int[Slots];
    private byte[] _softRuns = new byte[Slots * Slots], _softNext = new byte[Slots * Slots];
    private byte[] _hardRuns = new byte[Slots * Slots], _hardNext = new byte[Slots * Slots];
    private ulong _tick = ulong.MaxValue;

    public string Id => "movement.rootOverlap";
    public InvariantCost Cost => InvariantCost.EveryTick;
    public InvariantScope Scope => InvariantScope.Entity;
    public string After => GameStages.Movement;

    public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
    {
        var world = ctx.World;
        _fighters ??= world.Query().With<Fighter>().Without<Dead>().Read<Transform>();   // мёртвые (6г) — вне столкновений корней
        int n = 0;
        foreach (var chunk in world.Iterate(_fighters))
        {
            var entities = chunk.Entities;
            var t = chunk.Read<Transform>();
            for (int i = 0; i < entities.Length; i++)
            {
                if (n == _roots.Length)
                {
                    // Бойцов больше 33 — уже нарушение arena.slots; здесь всё равно проверяются все.
                    Array.Resize(ref _roots, n * 2);
                    Array.Resize(ref _entities, n * 2);
                    Array.Resize(ref _slots, n * 2);
                }
                var e = entities[i];
                (_roots[n], _entities[n]) = (t[i].Position, e);
                _slots[n] = world.TryGet<FighterSlot>(e, out var slot) && (uint)slot.Index < Slots ? slot.Index : -1;
                n++;
            }
        }

        // Плановая проверка нового тика продолжает счёт тиков подряд с прошлого (если тот был проверен); проверка по запросу
        // (invariant.run между тиками или посреди тика) и повтор того же тика счёт не сдвигают — берут его как есть.
        bool advance = ctx.Stage == After && ctx.Tick != _tick;
        if (advance)
        {
            if (ctx.Tick != _tick + 1)
            {
                Array.Clear(_softRuns);
                Array.Clear(_hardRuns);
            }
            Array.Clear(_softNext);
            Array.Clear(_hardNext);
        }
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                float l = (_roots[i] - _roots[j]).Length();   // та же мера, что в ResolveRoots
                if (l >= Diameter - Tolerance) continue;
                bool hard = l < HardLimit;
                // Пара без места (нарушение arena.slots) счёта не имеет: для неё правила — сразу.
                int soft = byte.MaxValue, deep = hard ? byte.MaxValue : 0;
                if (_slots[i] >= 0 && _slots[j] >= 0)
                {
                    int pair = Math.Min(_slots[i], _slots[j]) * Slots + Math.Max(_slots[i], _slots[j]);
                    if (advance)
                    {
                        _softNext[pair] = (byte)Math.Min(_softRuns[pair] + 1, byte.MaxValue);
                        if (hard) _hardNext[pair] = (byte)Math.Min(_hardRuns[pair] + 1, byte.MaxValue);
                        (soft, deep) = (_softNext[pair], _hardNext[pair]);
                    }
                    else (soft, deep) = (Math.Max((int)_softRuns[pair], 1), hard ? Math.Max((int)_hardRuns[pair], 1) : 0);
                }
                if (deep <= HardMaxTicks && soft <= MaxTicks) continue;
                // Проверка идёт до конца и при полном списке нарушений: счёт тиков подряд нужен всем парам.
                violations.Report(_entities[i], string.Create(CultureInfo.InvariantCulture,
                    $"корни {_entities[i]} и {_entities[j]} в {l:0.####} м: перекрытие {Diameter - l:0.####} м") +
                    (deep > HardMaxTicks
                        ? string.Create(CultureInfo.InvariantCulture, $" — корень в круге соседа (ближе {HardLimit:0.##} м) {deep} тиков подряд (можно {HardMaxTicks})")
                        : string.Create(CultureInfo.InvariantCulture, $" больше {Tolerance:0.###} м {soft} тиков подряд (можно {MaxTicks})")));
            }
        if (advance)
        {
            (_softRuns, _softNext) = (_softNext, _softRuns);
            (_hardRuns, _hardNext) = (_hardNext, _hardRuns);
            _tick = ctx.Tick;
        }
    }
}
