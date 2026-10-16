using System.Numerics;

namespace Sword.Engine.Physics;

// Члены трассы физики (класс объявлен в Contract.cs): включается владельцем (IPhysicsWorld.EnableTrace), пишут её крючки
// мира (PhysicsTrace.cs) и толчки (KinematicContacts.cs). Накопители — PhysicsTrace.Stats.cs.
public sealed partial class PhysicsTrace
{
    /// <summary>Источников поправок (<see cref="PhysicsSource"/>, в том числе самоколлизия).</summary>
    public const int SourceCount = 8;

    /// <summary>Фаз подшага (<see cref="PhysicsPhase"/>).</summary>
    public const int PhaseCount = 8;

    /// <summary>Причин пробуждения (<see cref="WakeReason"/>).</summary>
    public const int WakeReasonCount = 6;

    /// <summary>Порог события по умолчанию: тело за тик быстрее, м/с.</summary>
    public const float DefaultEventSpeed = 12f;

    /// <summary>Скорость не ниже этой доли потолка профиля — упор в потолок.</summary>
    public const float CappedShare = 0.999f;

    /// <summary>Гистограмма скоростей: корзина, м/с, и число корзин (последняя — всё быстрее).</summary>
    public const float SpeedBin = 0.25f;
    public const int SpeedBins = 256;

    /// <summary>Гистограмма угловых скоростей: корзина, рад/с, и число корзин.</summary>
    public const float AngularBin = 1f;
    public const int AngularBins = 128;

    /// <summary>Сколько самых быстрых событий держит каждый накопитель.</summary>
    public const int TopCapacity = 8;

    private float _eventSpeed;
    private readonly Stats _window = new(), _run = new();
    private Stats _last = new(), _step = new();

    private Contact[] _contacts, _contactsLast;
    private int _contactCount, _contactLastCount, _lost, _lostLast;

    private readonly Event[] _recent;
    private int _recentNext, _recentCount;

    // по слотам тел: снимки перед этапом подшага и данные тика для событий
    private Snap[] _snaps = [];
    private float[] _bodySpeed = [], _bodyAngular = [], _bodyHitDv = [], _bodyLinear = [], _bodyAngle = [];
    private int[] _touched = [];
    private bool[] _isTouched = [];
    private int _touchedCount, _bodyCapacity;

    // по слотам сочленений: энергия на физике (кинетическая + m·g·y) в конце прошлого подшага и сейчас; толкали ли живые; касалось ли
    // другого тела на физике
    private double[] _energyPrev = [], _energyNow = [];
    private bool[] _energyHas = [], _energyValid = [], _pushed = [], _paired = [];
    private int _artCapacity;
    private double _energyTotal, _energyPairedTotal;
    private bool _energyTotalAny, _energyPairedAny;

    // Энергия по фазам подшага — после прошлой фазы и сейчас (по слотам сочленений), рост фаз этого подшага (сочленение × фаза)
    private double[] _phaseNow = [], _phaseLast = [];
    private bool[] _phaseHas = [], _phaseLastValid = [], _phaseValid = [];
    private float[] _phaseGrowth = [];

    private readonly double[] _impulseTick = new double[SourceCount];

    /// <param name="eventSpeed">Порог события, м/с (<see cref="EventSpeed"/>).</param>
    /// <param name="contactCapacity">Контактов подшага в <see cref="LastContacts"/> (лишние — <see cref="LostContacts"/>).</param>
    /// <param name="recentEvents">Кольцо последних событий (<see cref="RecentEvent"/>).</param>
    public PhysicsTrace(float eventSpeed = DefaultEventSpeed, int contactCapacity = 1024, int recentEvents = 64)
    {
        EventSpeed = eventSpeed;
        ArgumentOutOfRangeException.ThrowIfNegative(contactCapacity);
        ArgumentOutOfRangeException.ThrowIfLessThan(recentEvents, 1);
        _contacts = new Contact[contactCapacity];
        _contactsLast = new Contact[contactCapacity];
        _recent = new Event[recentEvents];
    }

    /// <summary>Порог события, м/с (конечный, ≥ 0): тело, чья скорость в каком-то подшаге тика больше, — событие тика.</summary>
    public float EventSpeed
    {
        get => _eventSpeed;
        set
        {
            if (!float.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "порог — конечный, не меньше 0");
            _eventSpeed = value;
        }
    }

    /// <summary>
    /// Последний тик: шаг и то, что было после него до следующего шага (удары, проход, отдача). Объект подменяется каждым
    /// шагом — читать через свойство после шага, ссылку не держать.
    /// </summary>
    public Stats Last => _last;

    /// <summary>Окно — накопитель сценария: чистит владелец (<see cref="ClearWindow"/>), прогон при этом не трогается.</summary>
    public Stats Window => _window;

    /// <summary>Прогон — всё с включения трассы (или с <see cref="Clear"/>).</summary>
    public Stats Run => _run;

    /// <summary>Контакты последнего подшага последнего шага (оверлей <c>contacts</c>): только последний — иначе контакт повторился бы по подшагам.</summary>
    public ReadOnlySpan<Contact> LastContacts => _contactsLast.AsSpan(0, _contactLastCount);

    /// <summary>Контактов последнего подшага, которые не поместились в <see cref="LastContacts"/>.</summary>
    public int LostContacts => _lostLast;

    public int RecentEventCount => _recentCount;

    /// <summary>Событие из кольца последних: 0 — самое новое.</summary>
    public Event RecentEvent(int k)
    {
        if ((uint)k >= (uint)_recentCount) throw new ArgumentOutOfRangeException(nameof(k), k, $"событий в кольце {_recentCount}");
        int n = _recent.Length;
        return _recent[(_recentNext - 1 - k + n) % n];
    }

    /// <summary>Очистить окно (начало сценария); прогон и последний тик не трогаются.</summary>
    public void ClearWindow() => _window.Clear();

    /// <summary>Очистить всё: окно, прогон, последний тик, контакты, кольцо событий, данные текущего тика.</summary>
    public void Clear()
    {
        _window.Clear();
        _run.Clear();
        _last.Clear();
        _step.Clear();
        _contactCount = _contactLastCount = _lost = _lostLast = 0;
        _recentNext = _recentCount = 0;
        for (int k = 0; k < _touchedCount; k++) Untouch(_touched[k]);
        _touchedCount = 0;
        Array.Clear(_energyValid);
        Array.Clear(_pushed);
        Array.Clear(_paired);
        Array.Clear(_phaseLastValid);
        Array.Clear(_phaseValid);
        Array.Clear(_impulseTick);
        _energyTotal = _energyPairedTotal = 0;
        _energyTotalAny = _energyPairedAny = false;
    }

    // ——— запись (мир; внутри шага — в накопитель шага, после шага — в последний тик; всегда — в окно и прогон) ———

    /// <summary>Поза тела перед этапом подшага.</summary>
    internal readonly record struct Snap(Vector3 Position, Vector3 PrevPosition, Quaternion Rotation);

    /// <summary>Слоты мира выросли (и при включении): массивы по слотам — дорастить.</summary>
    internal void Grow(int articulations, int bodies)
    {
        if (bodies > _bodyCapacity)
        {
            Array.Resize(ref _snaps, bodies);
            Array.Resize(ref _bodySpeed, bodies);
            Array.Resize(ref _bodyAngular, bodies);
            Array.Resize(ref _bodyHitDv, bodies);
            Array.Resize(ref _bodyLinear, bodies * SourceCount);
            Array.Resize(ref _bodyAngle, bodies * SourceCount);
            Array.Resize(ref _touched, bodies);
            Array.Resize(ref _isTouched, bodies);
            _bodyCapacity = bodies;
        }
        if (articulations > _artCapacity)
        {
            Array.Resize(ref _energyPrev, articulations);
            Array.Resize(ref _energyNow, articulations);
            Array.Resize(ref _energyHas, articulations);
            Array.Resize(ref _energyValid, articulations);
            Array.Resize(ref _pushed, articulations);
            Array.Resize(ref _paired, articulations);
            Array.Resize(ref _phaseNow, articulations);
            Array.Resize(ref _phaseLast, articulations);
            Array.Resize(ref _phaseHas, articulations);
            Array.Resize(ref _phaseLastValid, articulations);
            Array.Resize(ref _phaseValid, articulations * PhaseCount);
            Array.Resize(ref _phaseGrowth, articulations * PhaseCount);
            _artCapacity = articulations;
        }
    }

    /// <summary>Сочленение в слоте создано или удаляется: его энергия и данные тика его тел — не от прошлого владельца слота.</summary>
    internal void ResetArticulation(int articulation, ReadOnlySpan<int> bodies)
    {
        if (articulation >= _artCapacity) return;
        _energyValid[articulation] = _energyHas[articulation] = _pushed[articulation] = _paired[articulation] = false;
        _phaseLastValid[articulation] = false;
        _phaseValid.AsSpan(articulation * PhaseCount, PhaseCount).Clear();
        foreach (int b in bodies)
            if (b < _bodyCapacity) ClearBody(b);
    }

    internal void Begin(int body, in Snap snap) => _snaps[body] = snap;

    internal ref readonly Snap Snapshot(int body) => ref _snaps[body];

    /// <summary>Этап <paramref name="source"/> сдвинул тело из позы <paramref name="before"/> (сдвиг и угол за подшаг, сдвиг прошлой позы, псевдоимпульс).</summary>
    internal void Correction(int body, PhysicsSource source, float h, in Snap before, Vector3 position, Vector3 prevPosition, Quaternion rotation,
        float mass)
    {
        float dx = Vector3.Distance(position, before.Position);
        float da = PhysicsMath.RotationVector(before.Rotation, rotation).Length();
        float dp = Vector3.Distance(prevPosition, before.PrevPosition);
        // сдвиг одной прошлой позы — скрытая скорость: пропускать его вместе с «нет поправки» нельзя
        if (dx < 1e-7f && da < 1e-6f && dp < 1e-7f) return;
        float lin = dx / h, ang = da / h, hid = dp / h;
        int i = (int)source;
        Touch(body);
        ref float bl = ref _bodyLinear[body * SourceCount + i];
        bl = MathF.Max(bl, lin);
        ref float ba = ref _bodyAngle[body * SourceCount + i];
        ba = MathF.Max(ba, ang);
        _impulseTick[i] += mass * lin;
        _step.Correction(i, lin, ang, hid);
        _window.Correction(i, lin, ang, hid);
        _run.Correction(i, lin, ang, hid);
    }

    internal void RecordContact(PhysicsSource source, BodyHandle body, BodyHandle other, Vector3 point, Vector3 normal, float depth, float solved)
    {
        int i = (int)source;
        _step.Contact(i, depth, solved);
        _window.Contact(i, depth, solved);
        _run.Contact(i, depth, solved);
        if (_contactCount < _contacts.Length) _contacts[_contactCount++] = new Contact(source, body, other, point, normal, depth, solved);
        else _lost++;
    }

    internal void Friction(bool held)
    {
        _step.Friction(held);
        _window.Friction(held);
        _run.Friction(held);
    }

    internal void VelocitySolved(PhysicsSource source, float normalSpeed)
    {
        int i = (int)source;
        float v = MathF.Abs(normalSpeed);
        _step.VelocitySolved(i, v);
        _window.VelocitySolved(i, v);
        _run.VelocitySolved(i, v);
    }

    /// <summary>Толчок живого применил импульс <paramref name="impulse"/> = λ/h, Н·с; выталкивание сверх толкающей точки, м/с; доля прохода (−1 — не проход).</summary>
    internal void Push(PhysicsSource source, int articulation, float impulse, float pop, float passShare)
    {
        int i = (int)source;
        _pushed[articulation] = true;
        _step.Push(i, impulse, pop, passShare);
        _window.Push(i, impulse, pop, passShare);
        _run.Push(i, impulse, pop, passShare);
    }

    /// <summary>Конец подшага толчков: наибольшая доля бюджета пары и сколько пар толкало.</summary>
    internal void PairBudget(float share, int pairs)
    {
        _step.Budget(share, pairs);
        _window.Budget(share, pairs);
        _run.Budget(share, pairs);
    }

    /// <summary>Из прохода списан импульс удара: доля бюджета прохода, потраченная с открытия (после шага — в последний тик).</summary>
    internal void PassSpent(float share)
    {
        _last.PassSpent(share);
        _window.PassSpent(share);
        _run.PassSpent(share);
    }

    /// <summary>Импульс удара в тело (после шага): доля Δv точки от скорости удара, урезан ли потолком и проходом, Δv и Δω тела.</summary>
    internal void Hit(int body, float dvShare, bool capped, bool budgeted, float dv, float dw)
    {
        Touch(body);
        _bodyHitDv[body] = MathF.Max(_bodyHitDv[body], dv);
        _last.Hit(dvShare, capped, budgeted, dv, dw);
        _window.Hit(dvShare, capped, budgeted, dv, dw);
        _run.Hit(dvShare, capped, budgeted, dv, dw);
    }

    /// <summary>Спящее проснулось: внутри шага — в накопитель шага, между шагами — в последний тик; всегда — в окно и прогон.</summary>
    internal void Wake(WakeReason reason, bool inStep)
    {
        (inStep ? _step : _last).Wake(reason);
        _window.Wake(reason);
        _run.Wake(reason);
    }

    /// <summary>Сочленение уснуло (конец шага).</summary>
    internal void Slept()
    {
        _step.Slept();
        _window.Slept();
        _run.Slept();
    }

    /// <summary>Неспящее тело на физике коснулось спящего (спящее — неподвижная опора) в подшаге.</summary>
    internal void SleepContact()
    {
        _step.SleepContact();
        _window.SleepContact();
        _run.SleepContact();
    }

    /// <summary>Спящих сочленений в конце шага.</summary>
    internal void AsleepAt(int count)
    {
        _step.AsleepAt(count);
        _window.AsleepAt(count);
        _run.AsleepAt(count);
    }

    /// <summary>Отдачу сочленения забрали (после шага): модуль сырого импульса, Н·с.</summary>
    internal void Reaction(ArticulationHandle articulation, float impulse)
    {
        _last.Reaction(articulation, impulse);
        _window.Reaction(articulation, impulse);
        _run.Reaction(articulation, impulse);
    }

    /// <summary>Скорость тела на физике в конце подшага.</summary>
    internal void Speed(int body, BodyHandle handle, float v, float w, bool capped)
    {
        Touch(body);
        _bodySpeed[body] = MathF.Max(_bodySpeed[body], v);
        _bodyAngular[body] = MathF.Max(_bodyAngular[body], w);
        _step.Speed(handle, v, w, capped);
        _window.Speed(handle, v, w, capped);
        _run.Speed(handle, v, w, capped);
    }

    internal void EnergyClear()
    {
        Array.Clear(_energyNow);
        Array.Clear(_energyHas);
    }

    internal void EnergyAdd(int articulation, double energy)
    {
        _energyNow[articulation] += energy;
        _energyHas[articulation] = true;
    }

    /// <summary>Энергия до шага (<c>TraceStepBegin</c>): от неё — рост за подшаг 0.</summary>
    internal void EnergyBaseline(int articulation)
    {
        _energyPrev[articulation] = _energyNow[articulation];
        _energyValid[articulation] = _energyHas[articulation];
        _pushed[articulation] = _paired[articulation] = false;
    }

    /// <summary>Сочленения <paramref name="a"/> и <paramref name="b"/> в этом подшаге касались друг друга телами на физике.</summary>
    internal void Paired(int a, int b)
    {
        _paired[a] = true;
        _paired[b] = true;
    }

    /// <summary>
    /// Конец подшага: рост энергии сочленения по классам — толкаемое живыми, в контакте с другим телом на физике (сумма пары —
    /// отдельно: энергия передаётся между трупами), без того и другого; по фазам — у не толкаемых.
    /// </summary>
    internal void EnergyCompare(int articulation, ArticulationHandle handle)
    {
        bool has = _energyHas[articulation];
        if (has && _energyValid[articulation])
        {
            float growth = (float)(_energyNow[articulation] - _energyPrev[articulation]);
            bool pushed = _pushed[articulation], paired = _paired[articulation];
            _step.Energy(handle, growth, pushed, paired);
            _window.Energy(handle, growth, pushed, paired);
            _run.Energy(handle, growth, pushed, paired);
            if (!pushed)
            {
                _energyTotal += growth;
                _energyTotalAny = true;
                if (paired)
                {
                    _energyPairedTotal += growth;
                    _energyPairedAny = true;
                }
                EnergyPhases(articulation, handle);
            }
        }
        _energyPrev[articulation] = _energyNow[articulation];
        _energyValid[articulation] = has;
        _pushed[articulation] = _paired[articulation] = false;
    }

    /// <summary>Рост энергии сочленения по фазам этого подшага — в накопители (наибольший по фазе и чей).</summary>
    private void EnergyPhases(int articulation, ArticulationHandle handle)
    {
        int row = articulation * PhaseCount;
        for (int p = 0; p < PhaseCount; p++)
        {
            if (!_phaseValid[row + p]) continue;
            float growth = _phaseGrowth[row + p];
            _step.Phase(p, growth, handle);
            _window.Phase(p, growth, handle);
            _run.Phase(p, growth, handle);
        }
    }

    /// <summary>Фаза подшага: суммы энергий по сочленениям — заново.</summary>
    internal void PhaseClear()
    {
        Array.Clear(_phaseNow);
        Array.Clear(_phaseHas);
    }

    internal void PhaseAdd(int articulation, double energy)
    {
        _phaseNow[articulation] += energy;
        _phaseHas[articulation] = true;
    }

    /// <summary>Конец фазы у сочленения: рост против прошлой фазы (у интеграции — против конца прошлого подшага).</summary>
    internal void PhaseEnd(int articulation, PhysicsPhase phase)
    {
        int i = articulation * PhaseCount + (int)phase;
        bool has = _phaseHas[articulation];
        bool first = phase == PhysicsPhase.Integrate;
        bool valid = has && (first ? _energyValid[articulation] : _phaseLastValid[articulation]);
        double before = first ? _energyPrev[articulation] : _phaseLast[articulation];
        _phaseValid[i] = valid;
        _phaseGrowth[i] = valid ? (float)(_phaseNow[articulation] - before) : 0;
        _phaseLast[articulation] = _phaseNow[articulation];
        _phaseLastValid[articulation] = has;
    }

    /// <summary>Конец подшага: рост энергии всех сочленений без толчков (и всех в контакте друг с другом), контакты подшага — в последние.</summary>
    internal void SubstepEnd()
    {
        if (_energyTotalAny)
        {
            float total = (float)_energyTotal;
            _step.EnergyTotal(total);
            _window.EnergyTotal(total);
            _run.EnergyTotal(total);
        }
        if (_energyPairedAny)
        {
            float paired = (float)_energyPairedTotal;
            _step.EnergyPairedTotal(paired);
            _window.EnergyPairedTotal(paired);
            _run.EnergyPairedTotal(paired);
        }
        _energyTotal = _energyPairedTotal = 0;
        _energyTotalAny = _energyPairedAny = false;
        Array.Clear(_phaseValid);
        (_contacts, _contactsLast) = (_contactsLast, _contacts);
        _contactLastCount = _contactCount;
        _lostLast = _lost;
        _contactCount = _lost = 0;
        _step.Substep();
        _window.Substep();
        _run.Substep();
    }

    internal int TouchedCount => _touchedCount;

    internal int TouchedAt(int k) => _touched[k];

    internal float BodySpeed(int body) => _bodySpeed[body];

    /// <summary>Тело быстрее порога в этом тике — событие: в кольцо последних, в счёт и «самые быстрые» накопителей.</summary>
    internal void AddEvent(int b, BodyHandle body, ArticulationHandle articulation, float mass)
    {
        var e = new Event(_run.Steps, body, articulation, mass, _bodySpeed[b], _bodyAngular[b], _bodyHitDv[b],
            _bodyLinear.AsSpan(b * SourceCount, SourceCount), _bodyAngle.AsSpan(b * SourceCount, SourceCount));
        _recent[_recentNext] = e;
        _recentNext = (_recentNext + 1) % _recent.Length;
        if (_recentCount < _recent.Length) _recentCount++;
        _step.AddEvent(in e);
        _window.AddEvent(in e);
        _run.AddEvent(in e);
    }

    /// <summary>Конец шага: псевдоимпульс тика по источникам, данные тел сброшены, накопитель шага становится последним тиком.</summary>
    internal void StepEnd(float dt)
    {
        for (int i = 0; i < SourceCount; i++)
        {
            float j = (float)_impulseTick[i];
            _impulseTick[i] = 0;
            if (!(j > 0)) continue;
            _step.ImpulseTick(i, j);
            _window.ImpulseTick(i, j);
            _run.ImpulseTick(i, j);
        }
        _step.Step(dt);
        _window.Step(dt);
        _run.Step(dt);
        for (int k = 0; k < _touchedCount; k++) Untouch(_touched[k]);
        _touchedCount = 0;
        (_last, _step) = (_step, _last);
        _step.Clear();
        Array.Clear(_energyValid);
    }

    private void Touch(int body)
    {
        if (_isTouched[body]) return;
        _isTouched[body] = true;
        _touched[_touchedCount++] = body;
    }

    /// <summary>Тело уходит из списка тика: данные — в ноль.</summary>
    private void Untouch(int b)
    {
        _isTouched[b] = false;
        ClearBody(b);
    }

    /// <summary>Данные тика тела — в ноль (в списке тика тело остаётся, если было: повторно не добавится).</summary>
    private void ClearBody(int b)
    {
        _bodySpeed[b] = _bodyAngular[b] = _bodyHitDv[b] = 0;
        _bodyLinear.AsSpan(b * SourceCount, SourceCount).Clear();
        _bodyAngle.AsSpan(b * SourceCount, SourceCount).Clear();
    }
}
