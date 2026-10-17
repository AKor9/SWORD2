namespace Sword.Game.Blood;

/// <summary>Капля лужи трупа: боец, строка ран, номер раны, объём, мл, номер капли раны (соль места).</summary>
public readonly record struct BloodPour(ulong Fighter, int Row, int Wound, float Volume, uint Salt);

/// <summary>
/// Сочение сильных ран (решения 2–4): у раны <see cref="BloodWound.Strong"/> — источник с темпом R₀ = урон · <see cref="BloodTuning.OozeRate"/>
/// мл/с, гаснущим линейно до нуля за <see cref="BloodTuning.OozeSeconds"/>; у трупа темп не ниже доли <see cref="BloodTuning.OozeCorpseShare"/>
/// от R₀ — пока труп лежит (до снятия бойца). Объём шага — точный интеграл темпа между временами шагов; общий предел темпа на бойца
/// <see cref="BloodTuning.OozeMaxRate"/>. Выпущенное: доля <see cref="BloodTuning.OozeSpotShare"/> — в пятно у раны (оно растёт), остальное
/// копится и уходит новым потёком (<see cref="BloodDrips.Feed"/>) порциями <see cref="BloodTuning.OozeFeedVolume"/>. Всё — функция записей
/// ран и тиков: два прогона дают одно и то же; место потёка — из хеша (зерно раны, номер подпитки). Лужа под трупом: с тика смерти из ран
/// трупа после задержки <see cref="BloodTuning.CorpsePoolDelay"/> (труп падает) выливается <see cref="BloodTuning.CorpsePoolMl"/> с темпом ∝ e^(−t/τ),
/// по ранам — по урону² (смертельная — с весом <see cref="LethalPourWeight"/>), каплями <see cref="BloodTuning.CorpsePourDropMl"/>
/// (<see cref="Pours"/> шага; полёт и пол — у модуля).
/// </summary>
public sealed class BloodOoze
{
    private struct Source
    {
        public uint Seed;
        public ulong Tick;
        public float Time;        // время с раны на прошлом шаге, с
        public float Pending;     // накоплено на потёк, мл
        public double Released;   // выпущено, мл
        public uint Feeds;
        public float Rate;        // темп на последнем шаге, мл/с
        public float PourPending; // лужа трупа: накоплено на каплю, мл
        public uint Pours;
    }

    private Source[] _sources = new Source[64 * BloodWounds.RingSize];
    private readonly float[] _step = new float[BloodWounds.RingSize];
    private ulong[] _deadKey = new ulong[64], _deadTick = new ulong[64];   // тик смерти + 1 (0 — жив)
    private float[] _poolTime = new float[64];
    private BloodPour[] _pours = new BloodPour[64];
    private int _pourCount;

    /// <summary>Капли лужи трупов последнего шага.</summary>
    public ReadOnlySpan<BloodPour> Pours => _pours.AsSpan(0, _pourCount);

    /// <summary>Вылито в лужи трупов с начала прогона, мл.</summary>
    public double Poured { get; private set; }

    /// <summary>Выпущено сочением с начала прогона, мл; потёков рождено.</summary>
    public double Released { get; private set; }
    public long Feeds { get; private set; }

    /// <summary>Сочащихся ран на последнем шаге и их суммарный темп, мл/с.</summary>
    public int Active { get; private set; }
    public float Rate { get; private set; }

    /// <summary>Выпущено раной (строка, номер в кольце), мл; рана сменилась — 0.</summary>
    public double ReleasedBy(BloodWounds wounds, int row, int index)
    {
        if (row >= _sources.Length / BloodWounds.RingSize) return 0;
        ref readonly var w = ref wounds.At(row, index);
        ref readonly var s = ref _sources[row * BloodWounds.RingSize + index];
        return s.Seed == w.Seed && s.Tick == w.Tick ? s.Released : 0;
    }

    /// <summary>Темп раны на последнем шаге, мл/с.</summary>
    public float RateOf(BloodWounds wounds, int row, int index)
    {
        if (row >= _sources.Length / BloodWounds.RingSize) return 0;
        ref readonly var w = ref wounds.At(row, index);
        ref readonly var s = ref _sources[row * BloodWounds.RingSize + index];
        return s.Seed == w.Seed && s.Tick == w.Tick ? s.Rate : 0;
    }

    /// <summary>∫₀ᵗ max(1 − s/T, f) ds — объём при R₀ = 1.</summary>
    public static double Integral(double t, double seconds, double floor)
    {
        if (!(t > 0)) return 0;
        double knee = seconds * (1 - floor);
        if (t <= knee) return t - t * t / (2 * seconds);
        return knee - knee * knee / (2 * seconds) + floor * (t - knee);
    }

    /// <summary>
    /// Шаг сочения на тике <paramref name="tick"/> (длина тика <paramref name="tickDt"/>, с; <paramref name="now"/> — часы крови для пятна):
    /// <paramref name="dead"/> — боец мёртв (труп). Пятна — в <paramref name="skin"/> (null — без кожи), потёки — в <paramref name="drips"/>.
    /// </summary>
    public void Step(ulong tick, float tickDt, float now, BloodWounds wounds, BloodTuning tuning, Func<ulong, bool> dead, BloodSkin? skin,
        BloodDrips? drips)
    {
        if (_sources.Length < wounds.Capacity * BloodWounds.RingSize) Array.Resize(ref _sources, wounds.Capacity * BloodWounds.RingSize);
        if (_deadKey.Length < wounds.Capacity)
        {
            Array.Resize(ref _deadKey, wounds.Capacity);
            Array.Resize(ref _deadTick, wounds.Capacity);
            Array.Resize(ref _poolTime, wounds.Capacity);
        }
        _pourCount = 0;
        Active = 0;
        Rate = 0;
        for (int row = 0; row < wounds.Capacity; row++)
            if (wounds.Used(row)) StepFighter(row, tick, tickDt, now, wounds, tuning, dead, skin, drips);
    }

    private void StepFighter(int row, ulong tick, float tickDt, float now, BloodWounds wounds, BloodTuning tuning, Func<ulong, bool> dead,
        BloodSkin? skin, BloodDrips? drips)
    {
        var list = wounds.Of(row);
        if (list.IsEmpty) return;
        bool corpse = dead(wounds.Key(row));
        double floor = corpse ? Math.Clamp(tuning.OozeCorpseShare, 0, 1) : 0, seconds = Math.Max(tuning.OozeSeconds, 1e-3f);
        float total = 0, longest = 0;
        for (int i = 0; i < list.Length; i++)
        {
            ref readonly var w = ref list[i];
            ref var s = ref _sources[row * BloodWounds.RingSize + i];
            if (s.Seed != w.Seed || s.Tick != w.Tick) s = new Source { Seed = w.Seed, Tick = w.Tick };
            _step[i] = 0;
            if (!w.Strong || tick < w.Tick) continue;
            float t = (tick - w.Tick) * tickDt;
            float r0 = w.Damage * tuning.OozeRate;
            _step[i] = (float)(r0 * (Integral(t, seconds, floor) - Integral(s.Time, seconds, floor)));
            longest = MathF.Max(longest, t - s.Time);
            s.Rate = (float)(r0 * Math.Max(1 - t / seconds, floor));
            s.Time = t;
            total += _step[i];
        }
        float cap = tuning.OozeMaxRate * longest;
        float scale = total > cap && total > 0 ? cap / total : 1;
        for (int i = 0; i < list.Length; i++)
            if (_step[i] > 0) Release(row, i, list[i], _step[i] * scale, tick, now, tuning, skin, drips);
        Pool(row, tick, tickDt, corpse, wounds, tuning);
    }

    /// <summary>Лужа трупа: объём шага — ∫ темпа PoolMl/τ·e^(−t/τ) от тика смерти + задержка, по ранам — по урону²; набралась капля — в <see cref="Pours"/>.</summary>
    private void Pool(int row, ulong tick, float tickDt, bool corpse, BloodWounds wounds, BloodTuning tuning)
    {
        ulong key = wounds.Key(row);
        if (!corpse || _deadKey[row] != key)
        {
            (_deadKey[row], _deadTick[row], _poolTime[row]) = (key, corpse ? tick + 1 : 0, 0);
            if (!corpse) return;
        }
        if (_deadTick[row] == 0) (_deadTick[row], _poolTime[row]) = (tick + 1, 0);
        // Пока труп падает (задержка), лужа не льётся: капли с летящего тела легли бы дорожкой, а не лужей.
        float tau = MathF.Max(tuning.CorpsePoolSeconds, 1e-3f), t = (tick + 1 - _deadTick[row]) * tickDt - MathF.Max(tuning.CorpsePoolDelay, 0);
        if (!(t > 0)) return;
        float volume = tuning.CorpsePoolMl * (MathF.Exp(-_poolTime[row] / tau) - MathF.Exp(-t / tau));
        _poolTime[row] = t;
        var list = wounds.Of(row);
        ulong lethal = 0;   // последняя рана живого — смертельная
        for (int i = 0; i < list.Length; i++)
            if (!list[i].Corpse && list[i].Tick >= lethal) lethal = list[i].Tick;
        float damage = 0;
        for (int i = 0; i < list.Length; i++) damage += PourWeight(list[i], lethal);
        if (!(volume > 0) || !(damage > 0)) return;
        float drop = MathF.Max(tuning.CorpsePourDropMl, 0.05f);
        for (int i = 0; i < list.Length; i++)
        {
            ref var s = ref _sources[row * BloodWounds.RingSize + i];
            s.PourPending += volume * PourWeight(list[i], lethal) / damage;
            if (s.PourPending < drop) continue;
            if (_pourCount == _pours.Length) Array.Resize(ref _pours, 2 * _pours.Length);
            _pours[_pourCount++] = new BloodPour(key, row, i, s.PourPending, s.Pours++);
            Poured += s.PourPending;
            s.PourPending = 0;
        }
    }

    /// <summary>Смертельная рана (последняя рана живого) льёт в лужу трупа с весом во столько раз больше.</summary>
    public const float LethalPourWeight = 6;

    /// <summary>
    /// Доля раны в луже трупа ∝ урон², у смертельной — × <see cref="LethalPourWeight"/>: льёт в основном одна рана — лужа, а не россыпь
    /// пятен по всем ранам (удары врага равны по урону).
    /// </summary>
    private static float PourWeight(in BloodWound w, ulong lethal)
    {
        float d = MathF.Max(w.Damage, 0);
        return d * d * (!w.Corpse && w.Tick == lethal ? LethalPourWeight : 1);
    }

    private void Release(int row, int index, in BloodWound w, float volume, ulong tick, float now, BloodTuning tuning, BloodSkin? skin,
        BloodDrips? drips)
    {
        ref var s = ref _sources[row * BloodWounds.RingSize + index];
        s.Released += volume;
        Released += volume;
        Active++;
        Rate += s.Rate;
        float spot = volume * Math.Clamp(tuning.OozeSpotShare, 0, 1);
        skin?.Spot(w.Fighter, w.Triangle, w.BindPoint, w.BindNormal, BloodModule.SpotRadius(w.Damage, tuning) * 0.6f, spot, now);
        s.Pending += volume - spot;
        if (s.Pending < tuning.OozeFeedVolume) return;
        drips?.Feed(w, index, s.Pending, tick, s.Feeds);
        s.Feeds++;
        Feeds++;
        s.Pending = 0;
    }

    /// <summary>
    /// Строка ран освобождена (боец снят или построен заново, ключ прежний): источники и лужа трупа строки — с нуля, иначе смерть
    /// возрождённого от первой же раны досталась бы луже прошлой жизни (почти вылитой).
    /// </summary>
    public void Clear(int row)
    {
        if ((uint)row >= (uint)_deadKey.Length) return;
        (_deadKey[row], _deadTick[row], _poolTime[row]) = (0, 0, 0);
        if ((row + 1) * BloodWounds.RingSize <= _sources.Length) Array.Clear(_sources, row * BloodWounds.RingSize, BloodWounds.RingSize);
    }

    public void ClearAll()
    {
        Array.Clear(_sources);
        Array.Clear(_deadKey);
        Array.Clear(_deadTick);
        _pourCount = 0;
    }
}
