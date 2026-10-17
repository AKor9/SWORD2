namespace Sword.Game.Blood;

/// <summary>
/// Сочение сильных ран (решения 2–4): у раны <see cref="BloodWound.Strong"/> — источник с темпом R₀ = урон · <see cref="BloodTuning.OozeRate"/>
/// мл/с, гаснущим линейно до нуля за <see cref="BloodTuning.OozeSeconds"/>; у трупа темп не ниже доли <see cref="BloodTuning.OozeCorpseShare"/>
/// от R₀ — пока труп лежит (до снятия бойца). Объём шага — точный интеграл темпа между временами шагов; общий предел темпа на бойца
/// <see cref="BloodTuning.OozeMaxRate"/>. Выпущенное: доля <see cref="BloodTuning.OozeSpotShare"/> — в пятно у раны (оно растёт), остальное
/// копится и уходит новым потёком (<see cref="BloodDrips.Feed"/>) порциями <see cref="BloodTuning.OozeFeedVolume"/>. Всё — функция записей
/// ран и тиков: два прогона дают одно и то же; место потёка — из хеша (зерно раны, номер подпитки).
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
    }

    private Source[] _sources = new Source[64 * BloodWounds.RingSize];
    private readonly float[] _step = new float[BloodWounds.RingSize];

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

    public void ClearAll() => Array.Clear(_sources);
}
