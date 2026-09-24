using System.Numerics;

namespace Sword.Engine.Physics;

// 7з-3: сон сочленений (решение пользователя 2026-09-24; README физики, «Устройство» → «Сон»; история — «7з-3»). Неподвижное
// сочленение засыпает: шаг его не считает, скорости — 0, поза, прошлая поза и поза начала тика — одна. Просыпается, когда его может
// что-то сдвинуть: до подшагов шага — тело живого
// или неспящее тело на физике у спящего движется быстрее порога части или вошло глубже допуска (толчок, касание), падающий труп под
// спящим (касание), цепочкой по куче на одно касание (проснувшееся будит касающихся спящих в том же тике); между шагами — удар, запись
// тела, режим, профиль, суставы, настройки, Wake, а запись, уводящая тело на физике, — и спящих, лежавших на нём (по позе до записи).
// Внутренние члены — с приставкой Sleep.
public sealed partial class PhysicsWorld
{
    /// <summary>
    /// Запас касания для сна, м (7з-3): тело живого или неспящее тело на физике, чья капсула ближе этого к капсуле спящего тела (сверх суммы
    /// радиусов; у движущегося — плюс путь за шаг), — «у спящего»: быстрее <see cref="PhysicsSettings.SleepBodySpeed"/> — будит его; касается
    /// проснувшееся — будит цепочкой. Засыпание — с запасом вдвое больше (гистерезис: сосед на границе запаса не будит только что уснувшего).
    /// </summary>
    public const float SleepMargin = 0.01f;

    /// <summary>
    /// Допуск проникновения в спящее тело, м (7з-3): тело живого (кинематическое — шаг его из спящего не выталкивает) или неспящее тело на
    /// физике, вошедшее в капсулу спящего глубже этого, будит его, как бы медленно ни двигалось. Меньше — спящее для него неподвижная опора
    /// (неспящее выталкивается из неё, как из земли; живое стоит, как стояло): живой, стоящий у трупа, покачивается в стойке на миллиметры и не
    /// будит кучу. Засыпание — при проникновении не глубже половины (гистерезис). Инвариант игры <c>physics.sleepContacts</c> — этот допуск.
    /// </summary>
    public const float SleepSlop = 0.01f;

    /// <summary>Доля гистерезиса засыпания (7з-3): сосед держит кандидата, если быстрее этой доли порога части, глубже этой доли допуска, ближе запаса, делённого на неё.</summary>
    private const float SleepHold = 0.5f;

    /// <summary>Спит ли сочленение (по слотам).</summary>
    private bool[] _sleepAsleep = [];

    /// <summary>Счётчик покоя, с (по слотам): подряд сочленение медленнее порогов; у спящего — считает дальше.</summary>
    private float[] _sleepRest = [];

    private int _sleepCount;

    /// <summary>
    /// Проснувшиеся после прошлой проверки (между шагами — удар, запись, суставы; в проверке — толчок, касание, цепочка): очередь цепочки по
    /// куче в начале шага; член очереди — пока его флаг стоит (удалённое сочленение снимает флаг).
    /// </summary>
    private int[] _sleepWoken = [];
    private bool[] _sleepInWoken = [];
    private int _sleepWokenCount;

    /// <summary>
    /// Засыпание в конце шага: кандидаты (покой не короче <see cref="PhysicsSettings.SleepTime"/>) и их состояние: <see cref="SleepCandidate"/>,
    /// <see cref="SleepHeldState"/> (сосед разбудил бы), <see cref="SleepSupported"/> (опирается на землю или спящих).
    /// </summary>
    private int[] _sleepCandidates = [];
    private byte[] _sleepState = [];

    private const byte SleepCandidate = 1, SleepHeldState = 2, SleepSupported = 3;

    /// <summary>Досягаемость сочленения, м: запас + наибольший путь его тела за шаг (по слотам; считают проверка пробуждения и засыпание).</summary>
    private float[] _sleepReach = [];

    /// <summary>Идёт шаг (трасса: пробуждения внутри шага — в накопитель шага, между шагами — в последний тик).</summary>
    private bool _stepRunning;

    /// <summary>Щупы и коробки неспящих сочленений уже сняты проверкой пробуждения этого шага (отсев кинематических в начале шага их не повторяет).</summary>
    private bool _sleepProbed;

    /// <summary>Касаний спящего тела неспящим телом на физике в последнем шаге (спящее — неподвижная опора) — тестам и диагностике.</summary>
    internal int SleepContacts { get; private set; }

    public bool IsAsleep(ArticulationHandle articulation)
    {
        Check(articulation);
        return _sleepAsleep[articulation.Index];
    }

    public float RestTime(ArticulationHandle articulation)
    {
        Check(articulation);
        return _sleepRest[articulation.Index];
    }

    public void Wake(ArticulationHandle articulation)
    {
        Check(articulation);
        SleepWake(articulation.Index, WakeReason.Command);
    }

    public int AsleepCount => _sleepCount;

    /// <summary>Слоты сочленений выросли: массивы сна — дорастить (зовёт <see cref="ContactsGrow"/>).</summary>
    private void SleepGrow(int articulations)
    {
        if (_sleepAsleep.Length >= articulations) return;
        Array.Resize(ref _sleepAsleep, articulations);
        Array.Resize(ref _sleepRest, articulations);
        Array.Resize(ref _sleepWoken, articulations);
        Array.Resize(ref _sleepInWoken, articulations);
        Array.Resize(ref _sleepCandidates, articulations);
        Array.Resize(ref _sleepState, articulations);
        Array.Resize(ref _sleepReach, articulations);
    }

    /// <summary>Сочленение в слоте создано или удаляется: не спит, покоя нет, из очереди цепочки — вон (зовёт <see cref="ContactsReset"/>).</summary>
    private void SleepReset(int a)
    {
        if (_sleepAsleep[a])
        {
            _sleepAsleep[a] = false;
            _sleepCount--;
        }
        _sleepRest[a] = 0;
        _sleepInWoken[a] = false;
    }

    // ——— Пробуждение ———

    /// <summary>Разбудить спящее сочленение (не спит — ничего): покой с нуля, в очередь цепочки, причина — трассе.</summary>
    private void SleepWake(int a, WakeReason reason)
    {
        if (!_sleepAsleep[a]) return;
        _sleepAsleep[a] = false;
        _sleepCount--;
        _sleepRest[a] = 0;
        if (!_sleepInWoken[a])
        {
            _sleepInWoken[a] = true;
            _sleepWoken[_sleepWokenCount++] = a;
        }
        _trace?.Wake(reason, _stepRunning);
    }

    /// <summary>Разбудить сочленение тела (запись тела, удар).</summary>
    private void SleepWakeBody(int body, WakeReason reason)
    {
        int a = _bodyOwner[body];
        if (a >= 0 && _sleepAsleep[a]) SleepWake(a, reason);
    }

    /// <summary>
    /// Тело на физике <paramref name="body"/> между шагами уходит из нынешней позы (<see cref="Place"/>, <see cref="Set"/> в другую позу или
    /// форму, <see cref="SetShape"/>), перестаёт быть телом на физике (<see cref="SetMode"/>, <see cref="Set"/>) или удаляется вместе с
    /// сочленением (<see cref="DestroyArticulation"/>): спящие чужие сочленения, чьих тел оно касается в нынешней позе (в запасе
    /// <see cref="SleepMargin"/>), могли на нём лежать — будить цепочкой сейчас, по позе до записи (цепочка в начале шага идёт уже от новой позы
    /// записанного). Найдено проверкой 7з-3: труп, уснувший на трупе, после возрождения нижнего (кинематика и постройка в другом месте) висел в
    /// воздухе, пока его самого не возродят. Разбуженные в очередь цепочки не встают — на одно касание, как <see cref="SleepChainFrom"/>; дальше
    /// будят, падая (<see cref="SleepFalling"/>). Кинематическое тело спящих не держит (шаг их не сталкивает) — его запись соседей не будит.
    /// </summary>
    private void SleepWakeAround(int body)
    {
        if (_sleepCount == 0) return;
        ref readonly var s = ref _bodies[body];
        if (s.Mode != BodyMode.Simulated) return;
        var axis = Vector3.Transform(Vector3.UnitY, s.Rotation) * s.HalfLength;
        var extent = Vector3.Abs(axis) + new Vector3(s.Radius + SleepMargin);
        int owner = _bodyOwner[body];
        for (int a = 0; a < _artCapacity && _sleepCount > 0; a++)
        {
            if (a == owner || !_sleepAsleep[a]) continue;
            var box = _contactsBox[a];
            if (!ContactsOverlap(s.Position, extent, box.Center, box.Half) || !SleepCapsuleTouches(s.Position, axis, extent, s.HalfLength, s.Radius, a)) continue;
            SleepWake(a, WakeReason.Chain);
            _sleepInWoken[a] = false;
        }
    }

    /// <summary>Капсула (центр, полуось, коробка с запасом, полудлина, радиус) ближе <see cref="SleepMargin"/> к какому-нибудь телу спящего <paramref name="a"/> (щупы — позы сна). NaN — нет.</summary>
    private bool SleepCapsuleTouches(Vector3 center, Vector3 axis, Vector3 extent, float halfLength, float radius, int a)
    {
        var members = _artBodies[a]!;
        for (int k = 0, n = _artCount[a]; k < n; k++)
        {
            int b = members[k];
            ref readonly var p = ref _contactsProbe[b];
            if (!ContactsOverlap(center, extent, p.Center, p.Extent)) continue;
            float d2 = ContactsClosest(center, axis, halfLength, p.Center, p.Axis, _bodies[b].HalfLength, out _, out _);
            if (MathF.Sqrt(d2) - radius - p.Radius < SleepMargin) return true;
        }
        return false;
    }

    /// <summary>Разбудить все спящие (настройки, земля, стена, колонны).</summary>
    private void SleepWakeAll(WakeReason reason)
    {
        if (_sleepCount == 0) return;
        for (int a = 0; a < _artCapacity; a++)
            if (_sleepAsleep[a]) SleepWake(a, reason);
    }

    /// <summary>Разбудить спящие сочленения, у которых есть тело с профилем <paramref name="profile"/> (профиль заменили).</summary>
    private void SleepWakeProfile(int profile)
    {
        if (_sleepCount == 0) return;
        for (int a = 0; a < _artCapacity; a++)
            if (_sleepAsleep[a] && SleepUsesProfile(a, profile)) SleepWake(a, WakeReason.Settings);
    }

    private bool SleepUsesProfile(int a, int profile)
    {
        var members = _artBodies[a]!;
        for (int k = 0, n = _artCount[a]; k < n; k++)
            if (_bodies[members[k]].Profile == profile) return true;
        return false;
    }

    /// <summary>
    /// До подшагов шага (7з-3): (1) неспящие сочленения (живые — кинематические, трупы — на физике) будят спящие, чьё тело их тело может
    /// сдвинуть (<see cref="SleepTouches"/>: у спящего — в запасе <see cref="SleepMargin"/> с путём за шаг — и движется в ближайшей точке оси
    /// быстрее <see cref="PhysicsSettings.SleepBodySpeed"/> или вошло глубже <see cref="SleepSlop"/>): живое — толчок, на физике — касание;
    /// падающий труп (<see cref="SleepFalling"/>) — и лежащих на нём при любой скорости (касание);
    /// (2) цепочка по куче — проснувшиеся здесь и между шагами (удар, команда, настройки) будят касающихся их спящих в том же тике, на одно
    /// касание: разбуженные цепочкой дальше будят своим движением (касание быстрее порога части) или падая, а не касанием. Почему не цепочка по всей куче
    /// — docs/history/engine-physics.md, «7з-3» (замер: вздрагивание одного трупа будило всю кучу). Обход — по слотам, очередь — по порядку пробуждения:
    /// детерминированно.
    /// </summary>
    private void SleepWakeBegin(float dt)
    {
        _sleepProbed = false;
        if (_sleepCount == 0)
        {
            SleepClearWoken();
            return;
        }
        // Будильники — все живые неспящие сочленения: щупы и коробки по нынешней позе (кинематические тела шаг не двигает — отсев пар их не
        // повторит), досягаемость — запас + наибольший путь тела за шаг.
        for (int a = 0; a < _artCapacity; a++)
            if (_artGeneration[a] > 0 && !_sleepAsleep[a]) SleepProbe(a, dt);
        _sleepProbed = true;

        // (1) толчки и касания: будильник, не проснувшийся после прошлой проверки (те — ниже, цепочкой)
        float speed = _settings.SleepBodySpeed;
        for (int w = 0; w < _artCapacity && _sleepCount > 0; w++)
            if (_artGeneration[w] > 0 && !_sleepAsleep[w] && !_sleepInWoken[w]) SleepWakeBy(w, dt, speed);

        // (2) цепочка по куче: проснувшийся будит касающихся спящих (на одно касание: разбуженные цепочкой в очередь не встают)
        for (int i = 0; i < _sleepWokenCount && _sleepCount > 0; i++)
        {
            int q = _sleepWoken[i];
            if (!_sleepInWoken[q] || _artGeneration[q] <= 0) continue;
            SleepProbe(q, dt);
            SleepChainFrom(q, dt);
        }
        SleepClearWoken();
    }

    private void SleepClearWoken()
    {
        for (int i = 0; i < _sleepWokenCount; i++) _sleepInWoken[_sleepWoken[i]] = false;
        _sleepWokenCount = 0;
    }

    /// <summary>Щупы и коробка неспящего сочленения по нынешней позе, досягаемость — запас + наибольший путь его тела за шаг.</summary>
    private void SleepProbe(int a, float dt)
    {
        ContactsRefresh(a);
        var members = _artBodies[a]!;
        float sweep = 0;
        for (int k = 0, n = _artCount[a]; k < n; k++) sweep = MathF.Max(sweep, SleepSweep(members[k], dt));
        _sleepReach[a] = SleepMargin + sweep;
    }

    /// <summary>
    /// Путь тела за шаг, м (7з-3; сверху): <c>(|v| + |ω|·полудлина)·dt</c>, у тела на физике — плюс <c>½·g·dt²</c> (разгон тяжестью). Нечисловое —
    /// 0 (его ловит счёт нечисловых; будить им нечего).
    /// </summary>
    private float SleepSweep(int body, float dt)
    {
        ref readonly var s = ref _bodies[body];
        float sweep = (s.Velocity.Length() + s.AngularVelocity.Length() * s.HalfLength) * dt;
        if (s.Mode == BodyMode.Simulated) sweep += 0.5f * MathF.Abs(_dynamics[s.Profile].Gravity) * dt * dt;
        return float.IsFinite(sweep) ? sweep : 0;
    }

    /// <summary>
    /// Будильник <paramref name="w"/> — по спящим по порядку слотов: толчок (тело живого) или касание (тело на физике); проснувшиеся — в очередь
    /// цепочки. Падающий труп (<see cref="SleepFalling"/>) будит и спящих, лежащих на нём (касание сверху — в запасе, с любой скоростью): у них
    /// уходит опора.
    /// </summary>
    private void SleepWakeBy(int w, float dt, float speed)
    {
        var box = _contactsBox[w];
        var reach = new Vector3(_sleepReach[w]);
        bool falling = SleepFalling(w);
        for (int s = 0; s < _artCapacity; s++)
        {
            if (!_sleepAsleep[s]) continue;
            var bs = _contactsBox[s];
            if (!ContactsOverlap(box.Center, box.Half + reach, bs.Center, bs.Half)) continue;
            int body = SleepTouches(w, s, dt, SleepMargin, speed, SleepSlop);
            if (body < 0 && falling) body = SleepTouches(w, s, dt, SleepMargin, 0, float.PositiveInfinity, above: true);
            if (body >= 0) SleepWake(s, _bodies[body].Mode == BodyMode.Simulated ? WakeReason.Touch : WakeReason.Push);
        }
    }

    /// <summary>
    /// Сочленение из одних тел на физике падает (найдено проверкой 7з-3): центр масс идёт вниз быстрее <see cref="PhysicsSettings.SleepSpeed"/>.
    /// Разбуженный цепочкой, у которого ушла опора, падает из покоя: зазор до лежащего на нём спящего (½·g·t²) обгоняет досягаемость раньше, чем
    /// его точка набирает <see cref="PhysicsSettings.SleepBodySpeed"/> (замер: 1,7 см против 1,5 при 0,57 м/с на 7-м тике) — без этого правила
    /// верхний повисал в воздухе. Будит только лежащих на нём (касание сверху): медленно лёгший на спящее будит его, как прежде, лишь быстрее
    /// порога части. Вздрагивание лёгкой части (стопа, клинок) центр масс трупа не двигает; живой (кинематический) — не падает.
    /// </summary>
    private bool SleepFalling(int w)
    {
        var members = _artBodies[w]!;
        float momentum = 0, mass = 0;
        for (int k = 0, n = _artCount[w]; k < n; k++)
        {
            ref readonly var s = ref _bodies[members[k]];
            if (s.Mode != BodyMode.Simulated) return false;
            momentum += s.Mass * s.Velocity.Y;
            mass += s.Mass;
        }
        return momentum < -_settings.SleepSpeed * mass;   // NaN — нет
    }

    /// <summary>
    /// Цепочка: проснувшийся <paramref name="q"/> будит спящих, чьих тел касается (в запасе с его путём за шаг), — без порога скорости; они в
    /// очередь цепочки не встают (цепочка — на одно касание).
    /// </summary>
    private void SleepChainFrom(int q, float dt)
    {
        var box = _contactsBox[q];
        var reach = new Vector3(_sleepReach[q]);
        for (int s = 0; s < _artCapacity; s++)
        {
            if (!_sleepAsleep[s]) continue;
            var bs = _contactsBox[s];
            if (!ContactsOverlap(box.Center, box.Half + reach, bs.Center, bs.Half)) continue;
            if (SleepTouches(q, s, dt, SleepMargin, 0, float.PositiveInfinity) < 0) continue;
            SleepWake(s, WakeReason.Chain);
            _sleepInWoken[s] = false;
        }
    }

    /// <summary>
    /// Тело сочленения <paramref name="w"/>, которое может сдвинуть тело сочленения <paramref name="s"/> (спящего или кандидата в сон): их
    /// капсулы ближе <paramref name="margin"/> + путь тела <paramref name="w"/> за шаг (сверх суммы радиусов), и тело <paramref name="w"/> в
    /// ближайшей точке оси движется быстрее <paramref name="speed"/> (0 — любое касание) или вошло в капсулу <paramref name="s"/> глубже
    /// <paramref name="slop"/>; <paramref name="above"/> — только касание сверху (ближайшая точка тела <paramref name="s"/> выше ближайшей точки
    /// тела <paramref name="w"/>: <paramref name="s"/> лежит на <paramref name="w"/>). Щупы обоих — нынешние. Нет — −1. Тела — по порядку сочленений.
    /// </summary>
    private int SleepTouches(int w, int s, float dt, float margin, float speed, float slop, bool above = false)
    {
        var mw = _artBodies[w]!;
        var ms = _artBodies[s]!;
        var bs = _contactsBox[s];
        int ns = _artCount[s];
        float speed2 = speed * speed;
        for (int x = 0, nw = _artCount[w]; x < nw; x++)
        {
            int bw = mw[x];
            ref readonly var pw = ref _contactsProbe[bw];
            float reach = margin + SleepSweep(bw, dt);
            var extent = pw.Extent + new Vector3(reach);
            if (!ContactsOverlap(pw.Center, extent, bs.Center, bs.Half)) continue;
            for (int y = 0; y < ns; y++)
            {
                int b = ms[y];
                ref readonly var ps = ref _contactsProbe[b];
                if (!ContactsOverlap(pw.Center, extent, ps.Center, ps.Extent)) continue;
                if (SleepPairTouches(bw, b, reach, speed2, slop, above)) return bw;
            }
        }
        return -1;
    }

    /// <summary>
    /// Пара тел: ближе <paramref name="reach"/> сверх радиусов (<paramref name="above"/> — ближайшая точка тела <paramref name="b"/> выше) и (глубже
    /// <paramref name="slop"/> или тело <paramref name="bw"/> в ближайшей точке оси быстрее порога).
    /// </summary>
    private bool SleepPairTouches(int bw, int b, float reach, float speed2, float slop, bool above)
    {
        ref readonly var pw = ref _contactsProbe[bw];
        ref readonly var ps = ref _contactsProbe[b];
        float d2 = ContactsClosest(pw.Center, pw.Axis, _bodies[bw].HalfLength, ps.Center, ps.Axis, _bodies[b].HalfLength, out var cw, out var cs);
        float gap = MathF.Sqrt(d2) - pw.Radius - ps.Radius;
        if (!(gap < reach)) return false;   // NaN — мимо
        if (above && !(cs.Y > cw.Y)) return false;
        if (-gap > slop || speed2 == 0) return true;
        return _bodies[bw].PointVelocity(cw).LengthSquared() > speed2;
    }

    // ——— Засыпание ———

    /// <summary>
    /// Конец шага (7з-3): счётчик покоя — у спящих дальше, у неспящих с одними телами на физике — плюс шаг, если сочленение медленнее порогов
    /// (<see cref="SleepStill"/>; иначе 0); покой не короче <see cref="PhysicsSettings.SleepTime"/> — кандидат. Кандидат засыпает, если рядом нет
    /// того, что его сразу разбудило бы, и он опирается на землю или спящих (<see cref="SleepDecide"/>). Сон выключен — ничего (спящих нет:
    /// смена настроек будит всех).
    /// </summary>
    private void SleepUpdate(float dt)
    {
        var settings = _settings;
        if (!settings.SleepEnabled) return;
        float speed2 = settings.SleepSpeed * settings.SleepSpeed, body2 = settings.SleepBodySpeed * settings.SleepBodySpeed, time = settings.SleepTime;
        int candidates = 0;
        for (int a = 0; a < _artCapacity; a++)
        {
            if (_artGeneration[a] <= 0) continue;
            if (_sleepAsleep[a])
            {
                _sleepRest[a] += dt;
                continue;
            }
            if (!SleepStill(a, speed2, body2))
            {
                _sleepRest[a] = 0;
                continue;
            }
            _sleepRest[a] += dt;
            if (_sleepRest[a] >= time) _sleepCandidates[candidates++] = a;
        }
        if (candidates > 0) SleepDecide(candidates, dt);
        _trace?.AsleepAt(_sleepCount);
    }

    /// <summary>
    /// Сочленение медленнее порогов сна (7з-3): все тела — на физике (кинематических нет); самая быстрая точка каждого тела (сверху:
    /// <c>|v| + |ω⊥|·полудлина + |ω|·радиус</c>, ω⊥ — поперёк оси) — медленнее <see cref="PhysicsSettings.SleepBodySpeed"/>, и сочленение целиком —
    /// среднеквадратичная скорость его массы <c>√(2·E/M)</c> (E — кинетическая энергия тел с вращением, M — масса) — медленнее
    /// <see cref="PhysicsSettings.SleepSpeed"/>. Строго; нечисловое — не медленнее. Почему не пороги скорости и угловой скорости каждого тела —
    /// docs/history/engine-physics.md, «7з-3» (замер: в куче у лёгких тел покоя нет — стопы и клинки крутятся вокруг своей оси на 1…2 рад/с и вздрагивают до
    /// 0,1…0,3 м/с, и куча с такими порогами не засыпала ни за 20 с).
    /// </summary>
    private bool SleepStill(int a, float speed2, float body2)
    {
        int n = _artCount[a];
        if (_contactsSimulated[a] != n) return false;
        var members = _artBodies[a]!;
        double energy = 0, mass = 0;
        for (int k = 0; k < n; k++)
        {
            ref readonly var s = ref _bodies[members[k]];
            float point = SleepPointSpeed(in s);
            if (!(point * point < body2)) return false;
            energy += s.KineticEnergy;
            mass += s.Mass;
        }
        return mass > 0 && 2 * energy < speed2 * mass;
    }

    /// <summary>
    /// Скорость самой быстрой точки капсулы, м/с (сверху): <c>|v| + |ω⊥|·полудлина + |ω|·радиус</c> — вращение вокруг своей оси двигает только
    /// поверхность (на радиусе), поперёк — и концы оси.
    /// </summary>
    internal static float SleepPointSpeed(in BodyState s)
    {
        var axis = s.Axis;
        var across = s.AngularVelocity - axis * Vector3.Dot(s.AngularVelocity, axis);
        return s.Velocity.Length() + across.Length() * s.HalfLength + s.AngularVelocity.Length() * s.Radius;
    }

    /// <summary>
    /// Кандидаты засыпают, если (1) никакой неспящий сосед (тело живого, неспящее тело на физике, кандидат, которого держит сосед) не разбудил
    /// бы их сразу — с гистерезисом <see cref="SleepHold"/>: быстрее половины порога части, глубже половины допуска, в двойном запасе; кандидаты
    /// рядом друг с другом засыпают вместе; и (2) опираются на землю (тело касалось земли в последнем подшаге), на спящих или на таких же
    /// опёртых кандидатов: на живом или неспящем трупе не засыпает — ушла бы опора, спящий повис бы в воздухе.
    /// </summary>
    private void SleepDecide(int candidates, float dt)
    {
        for (int i = 0; i < candidates; i++) _sleepState[_sleepCandidates[i]] = SleepCandidate;
        for (int a = 0; a < _artCapacity; a++)
            if (_artGeneration[a] > 0 && !_sleepAsleep[a]) SleepProbe(a, dt);

        // (1) держит ли сосед: держимый держит соседей-кандидатов дальше — до неподвижности
        float speed = SleepHold * _settings.SleepBodySpeed, margin = SleepMargin / SleepHold, slop = SleepHold * SleepSlop;
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int i = 0; i < candidates; i++)
            {
                int c = _sleepCandidates[i];
                if (_sleepState[c] != SleepCandidate || !SleepHeld(c, dt, margin, speed, slop)) continue;
                _sleepState[c] = SleepHeldState;
                changed = true;
            }
        }

        // (2) опора: земля или спящие, затем — через опёртых кандидатов
        for (int i = 0; i < candidates; i++)
        {
            int c = _sleepCandidates[i];
            if (_sleepState[c] == SleepCandidate && (SleepGrounded(c) || SleepTouchesAsleep(c, dt))) _sleepState[c] = SleepSupported;
        }
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int i = 0; i < candidates; i++)
            {
                int c = _sleepCandidates[i];
                if (_sleepState[c] != SleepCandidate || !SleepOnSupported(c, candidates, dt)) continue;
                _sleepState[c] = SleepSupported;
                changed = true;
            }
        }

        for (int i = 0; i < candidates; i++)
        {
            int c = _sleepCandidates[i];
            if (_sleepState[c] == SleepSupported) SleepFallAsleep(c);
            _sleepState[c] = 0;
        }
    }

    /// <summary>Кандидата <paramref name="c"/> держит неспящий сосед (не кандидат или держимый кандидат) — разбудил бы сразу.</summary>
    private bool SleepHeld(int c, float dt, float margin, float speed, float slop)
    {
        var bc = _contactsBox[c];
        for (int w = 0; w < _artCapacity; w++)
        {
            if (w == c || _artGeneration[w] <= 0 || _sleepAsleep[w] || _sleepState[w] is SleepCandidate or SleepSupported) continue;
            var box = _contactsBox[w];
            var reach = new Vector3(margin + _sleepReach[w] - SleepMargin);
            if (!ContactsOverlap(box.Center, box.Half + reach, bc.Center, bc.Half)) continue;
            if (SleepTouches(w, c, dt, margin, speed, slop) >= 0) return true;
        }
        return false;
    }

    /// <summary>Какое-нибудь тело сочленения касалось земли в последнем подшаге.</summary>
    private bool SleepGrounded(int a)
    {
        var members = _artBodies[a]!;
        for (int k = 0, n = _artCount[a]; k < n; k++)
            if (_bodies[members[k]].Grounded) return true;
        return false;
    }

    /// <summary>Кандидат касается спящего (в запасе).</summary>
    private bool SleepTouchesAsleep(int c, float dt)
    {
        var bc = _contactsBox[c];
        var reach = new Vector3(_sleepReach[c]);
        for (int s = 0; s < _artCapacity; s++)
        {
            if (!_sleepAsleep[s]) continue;
            var bs = _contactsBox[s];
            if (ContactsOverlap(bc.Center, bc.Half + reach, bs.Center, bs.Half) && SleepTouches(c, s, dt, SleepMargin, 0, float.PositiveInfinity) >= 0)
                return true;
        }
        return false;
    }

    /// <summary>Кандидат касается опёртого кандидата (в запасе).</summary>
    private bool SleepOnSupported(int c, int candidates, float dt)
    {
        var bc = _contactsBox[c];
        var reach = new Vector3(_sleepReach[c]);
        for (int j = 0; j < candidates; j++)
        {
            int o = _sleepCandidates[j];
            if (_sleepState[o] != SleepSupported) continue;
            var bo = _contactsBox[o];
            if (ContactsOverlap(bc.Center, bc.Half + reach, bo.Center, bo.Half) && SleepTouches(c, o, dt, SleepMargin, 0, float.PositiveInfinity) >= 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Уснуть: скорости — 0, прошлая поза и поза начала тика ← поза (клиент не качает застывшее тело интерполяцией; сдвиг последнего тика —
    /// меньше порога скорости × тик), щупы и коробка — по позе сна (дальше не меняются: запись тела будит).
    /// </summary>
    private void SleepFallAsleep(int a)
    {
        _sleepAsleep[a] = true;
        _sleepCount++;
        var members = _artBodies[a]!;
        for (int k = 0, n = _artCount[a]; k < n; k++)
        {
            ref var s = ref _bodies[members[k]];
            s.Velocity = s.AngularVelocity = Vector3.Zero;
            s.PrevPosition = s.StartPosition = s.Position;
            s.PrevRotation = s.StartRotation = s.Rotation;
        }
        ContactsRefresh(a);
        _trace?.Slept();
    }
}
