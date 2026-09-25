using System.Numerics;
using Sword.Engine.Animation.Motion;

namespace Sword.Game.Locomotion.Motion;

/// <summary>
/// Состояние Motion Matching бойца (компонент модуля — хеширует участник <c>ecs</c>, видно <c>entity.get</c>, <c>watch.add</c>,
/// <c>entity.history</c>, <c>replay.observe</c>): проигрыватель в хранилище модуля (<see cref="MotionMatchRuntime.Players"/> — его
/// состояние, кадры и смещения суставов хеширует участник <c>locomotion</c>), счётчики и память поиска, темп. Есть только у игрока на MM
/// (<c>locomotion.player = mm</c>). Все числа конечны (<c>ecs.finite</c>): нет продолжения — цена −1.
/// </summary>
public struct MotionMatchState
{
    /// <summary>Проигрыватель бойца; поколение 0 — ещё не заведён (заведёт первый шаг <c>locomotion.mm</c>).</summary>
    public MotionPlayerHandle Player;

    /// <summary>
    /// Боец построен (заново) — следующий шаг начинает с нуля: проигрыватель заново (не начат), история корня очищена, счётчики с нуля,
    /// первый кадр — поза постройки (<see cref="MotionMatchRuntime.StartFrame"/>) без инерциализации. Ставит <c>locomotion.mm-setup</c>
    /// по <c>Added&lt;Fighter&gt;</c> (появление, возрождение, R, команда сцены, пересборка) — не «телепорт».
    /// </summary>
    public bool Restart;

    /// <summary><c>AnimatorMotion.Search</c> прошлого тика: из false в true (кончилось действие всем телом) — поиск в этом тике.</summary>
    public bool SearchWas;

    /// <summary>Тиков с последнего поиска (целое: период поиска — целое число тиков).</summary>
    public int SinceSearch;

    /// <summary>Темп проигрывания этого тика: скорость корня / скорость корня клипа в текущем кадре, в 1 ± размах (<c>locomotion.rateRange</c>).</summary>
    public float Rate;

    /// <summary>
    /// Скорость управления, м/с: |цель скорости| модели контроллера, пока цель есть (от <see cref="GaspMotion.StandSpeed"/>); при остановке —
    /// последняя такая. Масштаб данных запроса — скорость клипов в этом направлении / она (<see cref="GaitSpeedModel"/>).
    /// </summary>
    public float ControlSpeed;

    /// <summary>Цель скорости (мир, м/с) на последнем поиске — для скачка цели.</summary>
    public Vector3 SearchGoal;

    /// <summary>Поисков и смен кадра поиском с постройки.</summary>
    public int Searches, Switches;

    /// <summary>
    /// Итог последнего поиска: лучший кадр базы, цена, цена продолжения (−1 — продолжения не было), осмотрено блоков и кадров, цены по 12
    /// группам схемы GASP.
    /// </summary>
    public MotionMatch Match;

    /// <summary>
    /// Вид после шага тика (пишет <c>locomotion.mm-history</c>, Movement — после исполнения заданий пула): кадр базы проигрывателя, клип
    /// библиотеки (<c>anim.clips {"index":N}</c> — имя), кадр в клипе, доля интервала, переходов с инерциализацией с постройки (по поиску и по
    /// рёбрам графа). Копия состояния хранилища — для агента; авторитетно хранилище.
    /// </summary>
    public int Frame, Clip, ClipFrame;

    /// <inheritdoc cref="Frame"/>
    public float Alpha;

    /// <inheritdoc cref="Frame"/>
    public int Transitions;

    /// <summary>Новое состояние: проигрывателя нет, первый шаг — с нуля.</summary>
    public static MotionMatchState Fresh => new() { Restart = true, Rate = 1, Frame = -1, Clip = -1, ClipFrame = -1, Match = NoMatch };

    /// <summary>Итог «поиска не было» — кадр −1, цены −1 (конечные).</summary>
    public static MotionMatch NoMatch => new(-1, -1, -1, 0, 0, default);
}
