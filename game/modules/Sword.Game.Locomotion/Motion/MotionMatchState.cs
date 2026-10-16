using System.Numerics;
using Sword.Engine.Animation.Motion;

namespace Sword.Game.Locomotion.Motion;

/// <summary>
/// Состояние Motion Matching бойца (хеширует участник <c>ecs</c>; проигрыватель в хранилище — участник <c>locomotion</c>).
/// Есть только у игрока на MM. Все числа конечны (<c>ecs.finite</c>): нет продолжения — цена −1.
/// </summary>
public struct MotionMatchState
{
    /// <summary>Проигрыватель бойца; поколение 0 — ещё не заведён (заведёт первый шаг <c>locomotion.mm</c>).</summary>
    public MotionPlayerHandle Player;

    /// <summary>
    /// Боец построен (заново) — следующий шаг начинает с нуля: проигрыватель заново, история очищена, первый кадр —
    /// <see cref="MotionMatchRuntime.StartFrame"/> без инерциализации. Ставит <c>locomotion.mm-setup</c>.
    /// </summary>
    public bool Restart;

    /// <summary><c>AnimatorMotion.Search</c> прошлого тика: из false в true (кончилось действие всем телом) — поиск в этом тике.</summary>
    public bool SearchWas;

    /// <summary>Тиков с последнего поиска (целое: период поиска — целое число тиков).</summary>
    public int SinceSearch;

    /// <summary>Темп проигрывания этого тика: скорость корня / скорость корня клипа в текущем кадре, в 1 ± размах (<c>locomotion.rateRange</c>).</summary>
    public float Rate;

    /// <summary>
    /// Скорость управления, м/с: |цель скорости| модели контроллера (при остановке — последняя); масштаб данных запроса —
    /// скорость клипов в этом направлении / она.
    /// </summary>
    public float ControlSpeed;

    /// <summary>Цель скорости (мир, м/с) на последнем поиске — для скачка цели.</summary>
    public Vector3 SearchGoal;

    /// <summary>Поисков и смен кадра поиском с постройки.</summary>
    public int Searches, Switches;

    /// <summary>Итог последнего поиска: кадр, цена, цена продолжения (−1 — не было), осмотрено, цены по 12 группам схемы.</summary>
    public MotionMatch Match;

    /// <summary>
    /// Вид после шага тика (пишет <c>locomotion.mm-history</c>): копия состояния хранилища для наблюдателей; авторитетно
    /// хранилище.
    /// </summary>
    public int Frame, Clip, ClipFrame;

    /// <inheritdoc cref="Frame"/>
    public float Alpha;

    /// <inheritdoc cref="Frame"/>
    public int Transitions;

    /// <summary>
    /// Поворот на месте (<see cref="TurnInPlace"/>): курс ног в мире, рад — стоя идёт за поворотом корня клипа (с подгонкой
    /// <see cref="TurnWarp"/>), на ходу — курс корня − <see cref="Twist"/>.
    /// </summary>
    public float FeetYaw;

    /// <summary>
    /// Расхождение δ = wrap(курс корня − курс ног) тика, рад, в пределах <c>locomotion.turnMax</c>: поза скручена на −δ;
    /// + — корень левее ног. <see cref="TwistBefore"/> — δ прошлого тика (замер скольжения стоп).
    /// </summary>
    public float Twist, TwistBefore;

    /// <summary>
    /// Выравнивание груди, рад: стоя — курс груди позы постройки − курс груди выхода, на ходу — 0; к цели не быстрее
    /// <see cref="GaspMotion.ChestRate"/>.
    /// </summary>
    public float Chest;

    /// <summary>Клип поворота на месте (номер библиотеки), пока корень клипа ещё поворачивается; −1 — поворота нет.</summary>
    public int TurnClip;

    /// <summary>Подгонка следующего шага: курс ног += поворот корня клипа за шаг × она (1 — вне поворота; 0…<see cref="GaspMotion.TurnWarpMax"/>).</summary>
    public float TurnWarp;

    /// <summary>Сколько курс корня стоит (стоя), с — для выдержки <c>locomotion.turnHold</c>.</summary>
    public float Still;

    /// <summary>Поворотов на месте (клипов поворота) с постройки.</summary>
    public int Turns;

    /// <summary>Новое состояние: проигрывателя нет, первый шаг — с нуля.</summary>
    public static MotionMatchState Fresh => new()
    {
        Restart = true, Rate = 1, Frame = -1, Clip = -1, ClipFrame = -1, Match = NoMatch, TurnClip = -1, TurnWarp = 1,
    };

    /// <summary>Итог «поиска не было» — кадр −1, цены −1 (конечные).</summary>
    public static MotionMatch NoMatch => new(-1, -1, -1, 0, 0, default);
}
