using System.Numerics;
using Sword.Engine.Diagnostics;
using Sword.Game.Contracts;

namespace Sword.Game.Fire;

/// <summary>
/// Огни тиков (без окна и GPU — проверяется тестами отдельно): после каждого тика читает оружие бойцов (<see cref="IWeaponTips"/>) и ведёт
/// огонь на каждом горящем оружии <b>игрока</b> (решение 5: враги без огня) — головку (кончик клинка тика), основание, центр шишки (на
/// <see cref="FireSettings.HeadOffset"/> ближе к кисти), <c>TickId</c> первого и последнего тика и последние <see cref="Points"/> головок
/// и центров по тикам. Оружие выпало при смерти — огонь
/// <see cref="Fire.Dying"/> («гаснет», решение 7), головка — на выпавшем оружии; боец построен в тике (<see cref="WeaponTip.Built"/>: сброс R
/// живого или мёртвого, команда сцены — ключ тот же, головка прыгает на новое место) — огонь заводится заново. Огонь, которого в тике не стало (другое оружие, убранное место), убирается. Тик — 0 Б: огни и буферы
/// заведены заранее.
/// </summary>
public sealed class FireTracker
{
    /// <summary>Головок по тикам у огня (кольцо): не меньше тиков кадра (<c>sim.maxTicksPerFrame</c> = 8) с запасом на шаги агента.</summary>
    public const int Points = 16;

    /// <summary>Огонь: владелец, оружие, головка тика, гаснет ли, тики и кольцо головок.</summary>
    public sealed class Fire
    {
        public ulong Key;
        public int Slot;
        public int Weapon;
        public string WeaponName = "";
        public Vector3 Head, Base;

        /// <summary>Центр шишки тика — точка огня (кисть, центр площадки).</summary>
        public Vector3 Center;
        public bool Dying;
        public ulong FirstTick, LastTick;
        public long Ticks;
        internal bool Seen;
        internal readonly Vector3[] Heads = new Vector3[Points];
        internal readonly Vector3[] Centers = new Vector3[Points];
        internal readonly ulong[] HeadTicks = new ulong[Points];
        internal int HeadCount, HeadNext;

        /// <summary>Подписи слоя снимка — заранее, без выделений в кадре.</summary>
        internal string Label = "", DyingLabel = "";

        /// <summary>Головка тика <paramref name="k"/>-го с конца (0 — последний тик), не больше <see cref="PointCount"/>.</summary>
        public (ulong Tick, Vector3 Head) Point(int k)
        {
            if ((uint)k >= (uint)HeadCount) throw new ArgumentOutOfRangeException(nameof(k));
            int i = (HeadNext - 1 - k + Points) % Points;
            return (HeadTicks[i], Heads[i]);
        }

        /// <summary>Центр шишки тика <paramref name="k"/>-го с конца (0 — последний тик).</summary>
        public Vector3 CenterAt(int k)
        {
            if ((uint)k >= (uint)HeadCount) throw new ArgumentOutOfRangeException(nameof(k));
            return Centers[(HeadNext - 1 - k + Points) % Points];
        }

        /// <summary>Сколько головок в кольце.</summary>
        public int PointCount => HeadCount;

        internal void Push(ulong tick, Vector3 head, Vector3 center)
        {
            Heads[HeadNext] = head;
            Centers[HeadNext] = center;
            HeadTicks[HeadNext] = tick;
            HeadNext = (HeadNext + 1) % Points;
            HeadCount = Math.Min(HeadCount + 1, Points);
        }
    }

    private readonly FireSettings _settings;
    private readonly IWeaponTips? _tips;
    private readonly bool[] _burning;
    private readonly Fire[] _fires;
    private int _count;
    private WeaponTip[] _buffer = new WeaponTip[4];

    public FireTracker(FireSettings settings, IWeaponTips? tips)
    {
        settings.Validate();
        _settings = settings;
        _tips = tips;
        _fires = new Fire[settings.MaxFires];
        for (int i = 0; i < _fires.Length; i++) _fires[i] = new Fire();
        int weapons = tips?.WeaponCount ?? 0;
        _burning = new bool[weapons];
        var burning = new List<string>();
        var missing = new List<string>();
        foreach (string name in settings.Weapons ?? [])
        {
            int w = tips?.FindWeapon(name) ?? -1;
            if (w >= 0 && w < weapons)
            {
                _burning[w] = true;
                burning.Add(tips!.WeaponName(w));
            }
            else missing.Add(name);
        }
        Burning = burning;
        Missing = missing;
        if (tips is null) Log.Warning("fire", "оружия бойцов нет (клиент игры не дал IWeaponTips) — огня не будет");
        else if (missing.Count > 0)
            Log.Warning("fire", $"fire.weapons: в таблице оружия нет {string.Join(", ", missing)} — не горит" +
                (burning.Count > 0 ? $"; горит: {string.Join(", ", burning)}" : "; огня не будет"));
        else Log.Info("fire", $"горит: {string.Join(", ", burning)}; огней не больше {settings.MaxFires}");
    }

    public FireSettings Settings => _settings;

    /// <summary>Горящее оружие, найденное в таблице (имена таблицы), и имена из конфига, которых там нет.</summary>
    public IReadOnlyList<string> Burning { get; }
    public IReadOnlyList<string> Missing { get; }

    /// <summary>Огни после последнего тика.</summary>
    public ReadOnlySpan<Fire> Fires => _fires.AsSpan(0, _count);

    /// <summary>Тиков, после которых огни читались.</summary>
    public long ObservedTicks { get; private set; }

    /// <summary>Огней, не заведённых сверх <see cref="FireSettings.MaxFires"/> (за прогон, по тикам).</summary>
    public long OverLimit { get; private set; }

    /// <summary>Тиков последнего кадра с тиками (<see cref="Frame"/>): столько последних головок — «последний шаг» оверлея.</summary>
    public int LastFrameTicks { get; private set; }

    /// <summary>После тика <paramref name="tick"/>: огни на горящем оружии игрока в мире этого тика.</summary>
    public void Observe(ulong tick)
    {
        ObservedTicks++;
        if (_tips is null) return;
        // Огонь — только у игрока (решение 5): прочие бойцы и их трупы не читаются.
        int n = _tips.Read(_buffer, playerOnly: true);
        if (n > _buffer.Length)
        {
            _buffer = new WeaponTip[Math.Max(n, _buffer.Length * 2)];
            n = _tips.Read(_buffer, playerOnly: true);
        }
        for (int f = 0; f < _count; f++) _fires[f].Seen = false;
        for (int i = 0; i < n; i++) Update(tick, _buffer[i]);
        // Огонь, которого в этом тике нет, — убрать (порядок остальных сохраняется).
        int kept = 0;
        for (int f = 0; f < _count; f++)
        {
            if (!_fires[f].Seen) continue;
            if (kept != f) (_fires[kept], _fires[f]) = (_fires[f], _fires[kept]);
            kept++;
        }
        _count = kept;
    }

    /// <summary>Кадр: сколько в нём тиков (извлечение кадра модуля; кадр без тиков — прежнее значение).</summary>
    public void Frame(int ticks)
    {
        if (ticks > 0) LastFrameTicks = ticks;
    }

    private void Update(ulong tick, in WeaponTip t)
    {
        if (!t.IsPlayer || (uint)t.Weapon >= (uint)_burning.Length || !_burning[t.Weapon]) return;
        Fire? fire = null;
        for (int f = 0; f < _count; f++)
            if (_fires[f].Key == t.Key && _fires[f].Weapon == t.Weapon)
            {
                fire = _fires[f];
                break;
            }
        if (fire is null)
        {
            if (_count == _fires.Length)
            {
                OverLimit++;
                return;
            }
            fire = _fires[_count++];
            Start(fire, tick, t);
        }
        else if (t.Built) Start(fire, tick, t);   // построен заново на той же сущности: прежние головки — с другого места
        fire.Seen = true;
        fire.Head = t.Tip;
        fire.Base = t.Base;
        fire.Center = FireArea.KnobCenter(t.Base, t.Tip, _settings.HeadOffset);
        fire.Dying = t.Dropped || !t.Alive;
        fire.LastTick = tick;
        fire.Ticks++;
        fire.Push(tick, t.Tip, fire.Center);
    }

    /// <summary>Новый огонь (редко: смена оружия, постройка заново после смерти) — подписи строятся здесь, а не в кадре.</summary>
    private void Start(Fire fire, ulong tick, in WeaponTip t)
    {
        fire.Key = t.Key;
        fire.Slot = t.Slot;
        fire.Weapon = t.Weapon;
        fire.WeaponName = _tips!.WeaponName(t.Weapon);
        fire.FirstTick = tick;
        fire.Ticks = 0;
        fire.HeadCount = fire.HeadNext = 0;
        fire.Label = $"{Owner(t.Slot)}: {fire.WeaponName}";
        fire.DyingLabel = fire.Label + " — гаснет";
    }

    /// <summary>Владелец словами: игрок (место 0) или враг N.</summary>
    public static string Owner(int slot) => slot == 0 ? "игрок" : $"враг {slot}";
}
