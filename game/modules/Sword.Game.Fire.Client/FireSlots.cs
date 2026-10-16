using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Client;

namespace Sword.Game.Fire;

/// <summary>Состояние экземпляра сима огня.</summary>
public enum FireState : byte
{
    /// <summary>Свободен: шагов нет, поле не показывается.</summary>
    Free,

    /// <summary>Горит: кисть рисует точку в центре шишки.</summary>
    Burning,

    /// <summary>Гаснет: кисть выключена, поле затухает до освобождения.</summary>
    Dying,
}

/// <summary>Флаги заявки шага.</summary>
[Flags]
public enum FireStepFlags : uint
{
    None = 0,

    /// <summary>Кисть рисует точку шишки.</summary>
    Burning = 1,

    /// <summary>Перед шагом — сброс поля, кисти и площадки (огонь заново).</summary>
    Reset = 2,
}

/// <summary>Заявка шага сима огня (наблюдатель тиков → GPU-симуляция слота): тик и номер шага, центр шишки тика, флаги.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct FireStep
{
    public ulong Tick, Step;
    public Vector3 Center;
    public FireStepFlags Flags;
}

/// <summary>
/// Экземпляр сима огня глазами CPU: чей огонь (боец, оружие, тик начала), состояние, центр шишки последнего тика и последнего шага,
/// шаги. Сим на GPU (<see cref="FireSim"/>) исполняет его заявки.
/// </summary>
public sealed class FireSlot(int index)
{
    public int Index { get; } = index;
    public FireState State { get; internal set; }

    /// <summary>Огонь: боец (ключ <c>WeaponTip.Key</c>), место (0 — игрок), оружие и тик начала (<see cref="FireTracker.Fire.FirstTick"/>).</summary>
    public ulong Key { get; internal set; }
    public int Owner { get; internal set; }
    public int Weapon { get; internal set; }
    public string WeaponName { get; internal set; } = "";
    public ulong FirstTick { get; internal set; }

    /// <summary>Центр шишки последнего тика — точка огня; оружия в мире нет (сменили) — последний, где оно было.</summary>
    public Vector3 Center { get; internal set; }

    /// <summary>Оружие огня в мире (в руке или выпало); иначе площадка стоит.</summary>
    public bool HasWeapon { get; internal set; }

    /// <summary>Центр площадки последнего шага (центр шишки на тике шага) и тик, номер этого шага.</summary>
    public Vector3 StepCenter { get; internal set; }
    public ulong StepTick { get; internal set; }
    public ulong Step { get; internal set; }

    /// <summary>Флаги заявки последнего шага (горит ли кисть, был ли сброс).</summary>
    public FireStepFlags StepFlags { get; internal set; }

    /// <summary>Шагов с начала огня и шагов, что он гаснет; номер шага, с которого гаснет.</summary>
    public long Steps { get; internal set; }
    public long FadeSteps { get; internal set; }
    public ulong FadeFrom { get; internal set; }

    /// <summary>Тик, после которого огонь гаснет (кисть выключена): от него считается угасание света.</summary>
    public ulong FadeTick { get; internal set; }

    /// <summary>Σρ поля последнего прочитанного шага (чтение GPU с задержкой) и номер этого шага; чтения ещё нет — null.</summary>
    public float? DensitySum { get; internal set; }
    public ulong DensityStep { get; internal set; }

    internal bool Matched, PendingReset;
    internal SimRequests? Requests;
}

/// <summary>
/// Огни по экземплярам сима (без GPU — проверяется тестами отдельно): после каждого тика и огней тика (<see cref="FireTracker"/>) решает,
/// какой огонь в каком экземпляре, горит он или гаснет, и на тиках шага (60 Гц по <c>TickId</c>) кладёт заявки шага. Новый огонь (оружие
/// в руку, боец построен заново — <c>Built</c>) — свободный экземпляр со сбросом; свободного нет — самый давно гаснущий; нет и его — огонь
/// не горит (<see cref="OverLimit"/>). Огонь, которого в тике нет (оружие сменили), и огонь выпавшего оружия или мёртвого бойца — гаснет:
/// кисть выключена, сим шагает <see cref="FireSettings.FadeSeconds"/> или пока Σρ не ниже <see cref="FireSettings.FadeDensity"/>, затем
/// экземпляр свободен. Пауза — тиков нет, шагов нет. Тик — 0 Б.
/// </summary>
public sealed class FireSlots
{
    private readonly FireSettings _settings;
    private readonly FireTracker _tracker;
    private readonly FireSlot[] _slots;
    private readonly int[] _pending;
    private readonly int _tickRate;
    private readonly long _fadeSteps;

    public FireSlots(FireSettings settings, FireTracker tracker, float tickDt)
    {
        settings.Validate();
        _settings = settings;
        _tracker = tracker;
        _slots = new FireSlot[settings.MaxFires];
        for (int i = 0; i < _slots.Length; i++) _slots[i] = new FireSlot(i);
        _pending = new int[FireSettings.FiresLimit + FireTracker.Points];
        _tickRate = FireArea.TickRate(tickDt);
        _fadeSteps = Math.Max(1, (long)Math.Round(settings.FadeSeconds * FireArea.StepRate));
    }

    public ReadOnlySpan<FireSlot> Slots => _slots;

    /// <summary>Тиков в секунду (такт шагов).</summary>
    public int TickRate => _tickRate;

    /// <summary>Шагов гаснущего огня не больше.</summary>
    public long FadeSteps => _fadeSteps;

    /// <summary>Огней, которым не хватило экземпляра (по тикам); экземпляров, отнятых у гаснущих; освобождено по Σρ и по времени.</summary>
    public long OverLimit { get; private set; }
    public long Stolen { get; private set; }
    public long ReleasedByDensity { get; private set; }
    public long ReleasedByTime { get; private set; }

    /// <summary>Заявок шага положено (за прогон).</summary>
    public long StepRequests { get; private set; }

    /// <summary>Заявки шага экземпляра <paramref name="slot"/> — в его GPU-симуляцию (без неё — только счёт).</summary>
    public void Bind(int slot, SimRequests requests) => _slots[slot].Requests = requests;

    /// <summary>Экземпляр по номеру.</summary>
    public FireSlot this[int slot] => _slots[slot];

    /// <summary>Статистика поля экземпляра (чтение GPU): по Σρ гаснущий огонь освобождается раньше срока.</summary>
    public void ReportDensity(int slot, ulong step, float densitySum)
    {
        var s = _slots[slot];
        s.DensitySum = densitySum;
        s.DensityStep = step;
    }

    /// <summary>После тика <paramref name="tick"/> и огней этого тика: экземпляры и заявки шага.</summary>
    public void Observe(ulong tick)
    {
        foreach (var s in _slots) s.Matched = false;
        var fires = _tracker.Fires;
        int pending = 0;
        for (int i = 0; i < fires.Length; i++)
        {
            var f = fires[i];
            var slot = Find(f);
            if (slot is null)
            {
                // Огонь заводится только горящим: выпавшее оружие, чей огонь погас (экземпляр освобождён), не загорается снова.
                if (!f.Dying && pending < _pending.Length) _pending[pending++] = i;
                continue;
            }
            Follow(slot, f);
        }
        // Огня нет в тике (оружие сменили) — гаснет там, где был; огонь начат заново — прежний экземпляр того же оружия гаснет.
        foreach (var s in _slots)
        {
            if (s.State == FireState.Free || s.Matched) continue;
            s.HasWeapon = false;
            if (s.State == FireState.Burning) StartFade(s, tick);
        }
        for (int p = 0; p < pending; p++)
        {
            var f = fires[_pending[p]];
            var slot = Allocate();
            if (slot is null)
            {
                OverLimit++;
                continue;
            }
            Start(slot, f);
            Follow(slot, f);
        }
        if (FireArea.IsStep(tick, _tickRate)) StepAll(tick, FireArea.StepOf(tick, _tickRate));
    }

    private FireSlot? Find(FireTracker.Fire f)
    {
        foreach (var s in _slots)
            if (s.State != FireState.Free && s.Key == f.Key && s.Weapon == f.Weapon && s.FirstTick == f.FirstTick) return s;
        return null;
    }

    private void Follow(FireSlot s, FireTracker.Fire f)
    {
        s.Matched = true;
        s.HasWeapon = true;
        s.Center = f.Center;
        if (f.Dying && s.State == FireState.Burning) StartFade(s, f.LastTick);
    }

    private void StartFade(FireSlot s, ulong tick)
    {
        s.State = FireState.Dying;
        s.FadeSteps = 0;
        s.FadeFrom = FireArea.StepOf(tick, _tickRate);
        s.FadeTick = tick;
    }

    /// <summary>Свободный экземпляр, иначе самый давно гаснущий (при равенстве — меньший номер), иначе null.</summary>
    private FireSlot? Allocate()
    {
        FireSlot? oldest = null;
        foreach (var s in _slots)
        {
            if (s.State == FireState.Free) return s;
            if (s.State == FireState.Dying && (oldest is null || s.FadeSteps > oldest.FadeSteps)) oldest = s;
        }
        if (oldest is not null) Stolen++;
        return oldest;
    }

    /// <summary>Новый огонь в экземпляре — подписи строятся здесь (редко), а не в кадре.</summary>
    private void Start(FireSlot s, FireTracker.Fire f)
    {
        s.State = FireState.Burning;
        s.Key = f.Key;
        s.Owner = f.Slot;
        s.Weapon = f.Weapon;
        s.WeaponName = f.WeaponName;
        s.FirstTick = f.FirstTick;
        s.Steps = s.FadeSteps = 0;
        s.FadeFrom = s.FadeTick = 0;
        s.DensitySum = null;
        s.PendingReset = true;
    }

    private void StepAll(ulong tick, ulong step)
    {
        foreach (var s in _slots)
        {
            if (s.State == FireState.Free) continue;
            if (s.State == FireState.Dying)
            {
                bool faded = s.DensitySum is { } sum && s.DensityStep > s.FadeFrom && sum < _settings.FadeDensity;
                if (s.FadeSteps >= _fadeSteps || faded)
                {
                    if (s.FadeSteps >= _fadeSteps) ReleasedByTime++;
                    else ReleasedByDensity++;
                    s.State = FireState.Free;
                    continue;
                }
            }
            var flags = (s.State == FireState.Burning ? FireStepFlags.Burning : 0) | (s.PendingReset ? FireStepFlags.Reset : 0);
            s.Requests?.Add(tick, new FireStep { Tick = tick, Step = step, Center = s.Center, Flags = flags });
            StepRequests++;
            s.PendingReset = false;
            s.StepCenter = s.Center;
            s.StepTick = tick;
            s.Step = step;
            s.StepFlags = flags;
            s.Steps++;
            if (s.State == FireState.Dying) s.FadeSteps++;
        }
    }
}
