using Sword.Engine.Animation.Motion;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Locomotion.Motion;

/// <summary>
/// Movement, после <c>core.root-collision</c> и до <c>locomotion.mm-history</c> (<c>locomotion.mm-events</c>): переходы проигрывателя
/// тика — события <see cref="MotionTransition"/> (журнал ECS: <c>events.history</c>; экспорт клиенту). Пул исполнил задания тика
/// (<c>core.animation-tick</c>), а вид состояния в компоненте ещё прошлого тика: переходов за тик — счётчик проигрывателя минус счётчик
/// компонента. Запрошенный поиском (диагностика: тик и кадр запроса) — событие «поиск» (откуда — текущий кадр поиска, куда — кадр входа
/// найденного, цены поиска, повод), запрошенный поворотом на месте — событие «поворот на месте» (откуда — текущий кадр, куда — первый кадр
/// клипа поворота); остальные — рёбра графа (порядок в тике — как в задании: ребро при продвижении, затем запрос): откуда
/// — кадр ребра от кадра прошлого тика вперёд по графу, куда — его вход, цена — кадр входа против признаков кадра ребра с весами поиска.
/// В режиме причинности (<c>state.why</c>) родитель события — решение «MM: …» с поводом (корень). Мёртвого не касается; 0 Б.
/// </summary>
public sealed class MotionMatchEventSystem(MotionMatchRuntime runtime) : IContinuousSystem
{
    public static readonly SystemLabel Label = new("locomotion.mm-events");

    /// <summary>Решения журнала причинности — пояснения (строки-константы: в тике не строятся).</summary>
    public const string StartNote = "MM: первый поиск после постройки", ScheduleNote = "MM: поиск по расписанию",
        GoalJumpNote = "MM: поиск — скачок цели скорости", AfterActionNote = "MM: поиск после действия всем телом",
        EdgeNote = "MM: ребро графа — вход продолжения", TurnNote = "MM: поворот на месте — клип поворота";

    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(Label).After(GameLabels.RootCollision).Before(MotionMatchSystems.History).Reads<MotionMatchState>().Emits<MotionTransition>();
        _query = s.Query().With<Fighter>().Without<Dead>().Read<MotionMatchState>();
    }

    public void Run(in SystemContext ctx)
    {
        long tick = (long)ctx.World.Resource<SimTime>().Tick;
        foreach (var chunk in ctx.Iterate(_query))
        {
            var state = chunk.Read<MotionMatchState>();
            var entities = chunk.Entities[..state.Length];
            for (int i = 0; i < state.Length; i++) Emit(ctx, entities[i], state[i], tick);
        }
    }

    /// <summary>События бойца (тело цикла — свой метод: правило OSR).</summary>
    private void Emit(in SystemContext ctx, Entity e, in MotionMatchState s, long tick)
    {
        var players = runtime.Players;
        if (s.Restart || !players.IsAlive(s.Player)) return;
        var st = players.State(s.Player);
        int count = st.Transitions - s.Transitions;
        if (count <= 0 || s.Frame < 0) return;
        var search = runtime.Diagnostics.Last(s.Player);
        bool turned = runtime.Diagnostics.TurnedAt(s.Player, tick, out int turnFrom, out _);
        bool requested = turned || search is not null && search.RequestedTick == tick;
        int transitions = s.Transitions;
        if (count > (requested ? 1 : 0))
        {
            var (from, to) = Edge(runtime.Database, s.Frame);
            float cost = from >= 0 && to >= 0 ? runtime.Matcher.Cost(runtime.Database.FeatureRow(from), runtime.Weights, to, out _) : -1;
            Send(ctx, e, MotionTransitionKind.Edge, MotionSearchReason.None, from, to, cost, -1, ++transitions, EdgeNote);
        }
        if (turned)
            Send(ctx, e, MotionTransitionKind.TurnInPlace, MotionSearchReason.None, turnFrom, st.Frame, -1, -1, st.Transitions, TurnNote);
        else if (requested)
        {
            var match = search!.Match;
            Send(ctx, e, MotionTransitionKind.Search, search.Reason, search.Current, st.Frame, match.Cost, match.ContinuationCost,
                st.Transitions, Note(search.Reason));
        }
    }

    private void Send(in SystemContext ctx, Entity e, MotionTransitionKind kind, MotionSearchReason reason, int from, int to, float cost,
        float continuation, int transitions, string note)
    {
        var db = runtime.Database;
        if (ctx.Causality is not null) ctx.Cause = ctx.Commands.Note(note, e, CauseId.None, to);
        ctx.Commands.Emit(new MotionTransition
        {
            Fighter = e, Kind = kind, Reason = reason, From = from, To = to,
            FromClip = from >= 0 ? db.Clips[db.ClipOf[from]].LibraryClip : -1, ToClip = to >= 0 ? db.Clips[db.ClipOf[to]].LibraryClip : -1,
            FromClipFrame = from >= 0 ? db.FrameInClip(from) : -1, ToClipFrame = to >= 0 ? db.FrameInClip(to) : -1,
            Cost = float.IsFinite(cost) ? cost : -1, ContinuationCost = float.IsFinite(continuation) ? continuation : -1, Transitions = transitions,
        });
    }

    private static string Note(MotionSearchReason reason) => reason switch
    {
        MotionSearchReason.Start => StartNote,
        MotionSearchReason.GoalJump => GoalJumpNote,
        MotionSearchReason.AfterAction => AfterActionNote,
        _ => ScheduleNote,
    };

    /// <summary>
    /// Ребро графа, до которого проигрыватель дошёл от кадра <paramref name="frame"/> (прошлый тик): вперёд по <see cref="MotionDatabase.Next"/>
    /// внутри клипа до первого ребра — следующий кадр в другом клипе (поза стояла на последнем кадре — откуда он) или кадр выхода
    /// (<see cref="MotionDatabase.Entry"/> ≥ 0 — откуда он сам); куда — вход (цепочка входов — с предохранителем). Не нашлось — (−1, −1).
    /// </summary>
    public static (int From, int To) Edge(MotionDatabase db, int frame)
    {
        var next = db.Next;
        var entry = db.Entry;
        var clipOf = db.ClipOf;
        for (int n = 0, f = frame; n < 256 && f >= 0; n++)
        {
            int to = next[f];
            if (to < 0) return (-1, -1);
            if (clipOf[to] != clipOf[f]) return (f, Resolve(entry, to));
            if (entry[to] >= 0) return (to, Resolve(entry, to));
            f = to;
        }
        return (-1, -1);
    }

    private static int Resolve(ReadOnlySpan<int> entry, int frame)
    {
        for (int n = 0; n < 16 && entry[frame] >= 0; n++) frame = entry[frame];
        return frame;
    }
}
