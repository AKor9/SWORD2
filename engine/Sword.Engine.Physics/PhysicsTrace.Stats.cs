using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Sword.Engine.Physics;

// 7д: накопители трассы физики (последний тик, окно, прогон), событие, контакт. Все массивы заданы при создании: запись не
// выделяет памяти; строки — только в Summary и ToString (по запросу).
public sealed partial class PhysicsTrace
{
    private static readonly string[] SourceNames = ["суставы", "земля", "стена", "пара", "толчок тела", "толчок прохода", "без отдачи", "самоколлизия"];

    private static readonly string[] PhaseNames = ["интеграция", "суставы", "мир", "пары", "мир после пар", "скорости", "скоростной проход", "расслабление"];

    /// <summary>Имя источника для сводки.</summary>
    public static string SourceName(PhysicsSource source) => SourceNames[(int)source];

    /// <summary>Имя фазы подшага для сводки (7з-2).</summary>
    public static string PhaseName(PhysicsPhase phase) => PhaseNames[(int)phase];

    /// <summary>
    /// Контакт подшага: источник, тело на физике, второе тело (<see cref="BodyHandle.None"/> — мир: земля, стена, колонна;
    /// у толчка — кинематическое), точка на теле и нормаль к нему (мир), настоящая глубина и решённая за подшаг, м.
    /// </summary>
    public readonly record struct Contact(PhysicsSource Source, BodyHandle Body, BodyHandle Other, Vector3 Point, Vector3 Normal, float Depth,
        float Solved);

    /// <summary>
    /// Накопленное по источнику поправок: поправок записано; наибольшие сдвиг за подшаг / h (м/с), угол / h (рад/с), сдвиг
    /// прошлой позы / h (скрытый, должен быть 0); псевдоимпульс <c>Σ m·сдвиг/h</c> за тик — наибольший и сумма, Н·с;
    /// контактов, наибольшая настоящая и отложенная (глубина − решённая) глубина, м; наибольшая скорость, погашенная
    /// скоростным проходом, м/с; точный импульс толчков <c>Σλ/h</c>, Н·с.
    /// </summary>
    public struct SourceStats
    {
        public int Count { readonly get; internal set; }
        public float Linear { readonly get; internal set; }
        public float Angular { readonly get; internal set; }
        public float Hidden { readonly get; internal set; }
        public float ImpulseTickMax { readonly get; internal set; }
        public float ImpulseSum { readonly get; internal set; }
        public int Contacts { readonly get; internal set; }
        public float Depth { readonly get; internal set; }
        public float Deferred { readonly get; internal set; }
        public float VelocitySolved { readonly get; internal set; }
        public float Applied { readonly get; internal set; }
    }

    [InlineArray(SourceCount)]
    internal struct SourceFloats
    {
        private float _element;
    }

    /// <summary>
    /// Событие: тело на физике в тике шага <see cref="Step"/> (номер от начала прогона) было быстрее порога: наибольшие за тик
    /// скорость и угловая скорость, поправки этого тика по источникам (сдвиг / h, угол / h), Δv от удара перед шагом (0 — не
    /// было).
    /// </summary>
    public struct Event
    {
        private SourceFloats _linear, _angular;

        internal Event(long step, BodyHandle body, ArticulationHandle articulation, float mass, float speed, float angularSpeed, float hitDv,
            ReadOnlySpan<float> linear, ReadOnlySpan<float> angular)
        {
            Step = step;
            Body = body;
            Articulation = articulation;
            Mass = mass;
            Speed = speed;
            AngularSpeed = angularSpeed;
            HitDv = hitDv;
            linear.CopyTo(_linear);
            angular.CopyTo(_angular);
        }

        public long Step { readonly get; private set; }
        public BodyHandle Body { readonly get; private set; }
        public ArticulationHandle Articulation { readonly get; private set; }
        public float Mass { readonly get; private set; }
        public float Speed { readonly get; private set; }
        public float AngularSpeed { readonly get; private set; }
        public float HitDv { readonly get; private set; }

        /// <summary>Наибольший сдвиг тела источником за подшаг этого тика / h, м/с.</summary>
        public readonly float Linear(PhysicsSource source) => _linear[(int)source];

        /// <summary>Наибольший угол поворота тела источником за подшаг этого тика / h, рад/с.</summary>
        public readonly float Angular(PhysicsSource source) => _angular[(int)source];

        /// <summary>Строка события: тело, скорости, заметные поправки по убыванию, удар.</summary>
        public override readonly string ToString()
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append(inv, $"шаг {Step}: тело {Body} (сочленение {Articulation}), {Mass:F2} кг: v {Speed:F1} м/с, ω {AngularSpeed:F0} рад/с |");
            Span<bool> used = stackalloc bool[SourceCount];
            int shown = 0;
            while (true)
            {
                int best = -1;
                for (int i = 0; i < SourceCount; i++)
                    if (!used[i] && (_linear[i] > 0.05f || _angular[i] > 0.5f) && (best < 0 || _linear[i] > _linear[best])) best = i;
                if (best < 0) break;
                used[best] = true;
                shown++;
                sb.Append(inv, $" {SourceNames[best]} {_linear[best]:F1} м/с, {_angular[best]:F0} рад/с;");
            }
            if (shown == 0) sb.Append(" поправок нет;");
            if (HitDv > 0) sb.Append(inv, $" удар Δv {HitDv:F1} м/с;");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Накопитель трассы: последний тик (<see cref="Last"/>), окно (<see cref="Window"/>) или прогон (<see cref="Run"/>).
    /// Наибольшие значения — «хуже всего за время накопителя»; ноль — не было.
    /// </summary>
    public sealed class Stats
    {
        private readonly SourceStats[] _sources = new SourceStats[SourceCount];
        private readonly long[] _speedHistogram = new long[SpeedBins], _angularHistogram = new long[AngularBins];
        private readonly Event[] _top = new Event[TopCapacity];
        private int _topCount;
        private readonly float[] _phaseGrowth = new float[PhaseCount];
        private readonly ArticulationHandle[] _phaseArticulation = new ArticulationHandle[PhaseCount];
        private readonly int[] _wakes = new int[WakeReasonCount];

        /// <summary>Шагов (закрытых <c>TraceStep</c>) и подшагов (закрытых <c>TraceSubstep</c>); время шагов, с.</summary>
        public int Steps { get; private set; }
        public int Substeps { get; private set; }
        public double Seconds { get; private set; }

        /// <summary>По источнику поправок.</summary>
        public SourceStats Source(PhysicsSource source) => _sources[(int)source];

        /// <summary>Наибольшая скорость тела на физике в конце подшага, м/с, и чья; наибольшая угловая, рад/с.</summary>
        public float SpeedMax { get; private set; }
        public BodyHandle SpeedBody { get; private set; }
        public float AngularSpeedMax { get; private set; }

        /// <summary>Упоров в потолок профиля (скорость или угловая ≥ <see cref="CappedShare"/> потолка) — тело·подшагов.</summary>
        public int Capped { get; private set; }

        /// <summary>Замеров скорости (тело·подшагов) и из них нечисловых.</summary>
        public long SpeedSamples { get; private set; }
        public int NonFinite { get; private set; }

        /// <summary>Гистограмма скоростей тел на физике по подшагам (корзина <see cref="SpeedBin"/>).</summary>
        public ReadOnlySpan<long> SpeedHistogram => _speedHistogram;

        /// <summary>Гистограмма угловых скоростей (корзина <see cref="AngularBin"/>).</summary>
        public ReadOnlySpan<long> AngularHistogram => _angularHistogram;

        /// <summary>Перцентиль скорости <paramref name="q"/> (0…1) — верхняя граница корзины, м/с.</summary>
        public float SpeedPercentile(float q) => Percentile(_speedHistogram, SpeedBin, q);

        /// <summary>Перцентиль угловой скорости, рад/с.</summary>
        public float AngularPercentile(float q) => Percentile(_angularHistogram, AngularBin, q);

        /// <summary>Наибольшая доля бюджета пары за подшаг (≤ 1) и сколько раз пара толкала в подшаге (пар·подшагов).</summary>
        public float PairShare { get; private set; }
        public int PairSubsteps { get; private set; }

        /// <summary>Наибольшая доля бюджета прохода, потраченная с открытия (≤ 1): толчки клинка и удары.</summary>
        public float PassShare { get; private set; }

        /// <summary>Наибольшая скорость точки тела вдоль нормали после толчка сверх толкающей точки, м/с (предел — <see cref="PhysicsSettings.PopSpeed"/>).</summary>
        public float PushPopSpeed { get; private set; }

        /// <summary>Наибольшая забранная отдача (сырой импульс, Н·с) и чья; сумма, Н·с.</summary>
        public float ReactionMax { get; private set; }
        public ArticulationHandle ReactionArticulation { get; private set; }
        public float ReactionSum { get; private set; }

        /// <summary>Ударов; урезанных потолком Δv и проходом.</summary>
        public int Hits { get; private set; }
        public int HitsCapped { get; private set; }
        public int HitsBudgeted { get; private set; }

        /// <summary>Наибольшая доля Δv точки удара от скорости удара (предел — <see cref="PhysicsSettings.HitDvShare"/>); Δv и Δω тела.</summary>
        public float HitDvShare { get; private set; }
        public float HitDv { get; private set; }
        public float HitDw { get; private set; }

        /// <summary>Касаний торцов с трением покоя и сколько из них держало.</summary>
        public int FrictionContacts { get; private set; }
        public int FrictionHeld { get; private set; }

        /// <summary>
        /// Рост энергии за подшаг (кинетическая + m·g·y), Дж: наибольший у сочленения, которое живые в подшаге не толкали и которое не
        /// касалось другого тела на физике (7з-2; инвариант <c>physics.energy</c>), и какого; наибольший у всех без толчков вместе (и
        /// касавшихся); наибольший у толкаемого живыми (толчок вправе добавлять энергию).
        /// </summary>
        public float EnergyGrowth { get; private set; }
        public ArticulationHandle EnergyArticulation { get; private set; }
        public float EnergyGrowthTotal { get; private set; }
        public float EnergyGrowthPushed { get; private set; }

        /// <summary>
        /// Рост энергии за подшаг у сочленения, касавшегося другого тела на физике (7з-2: труп на трупе — энергия передаётся, рост одного —
        /// не накачка), Дж, и какого; наибольший рост суммы всех касавшихся за подшаг (передача внутри суммы сокращается).
        /// </summary>
        public float EnergyGrowthPaired { get; private set; }
        public ArticulationHandle EnergyPairedArticulation { get; private set; }
        public float EnergyGrowthPairedTotal { get; private set; }

        /// <summary>
        /// Рост энергии сочленения без толчков живых за фазу подшага <paramref name="phase"/> (7з-2), Дж — наибольший (где энергия
        /// появляется: суставы сводят разрыв, выталкивание, скоростной проход…), и у какого сочленения.
        /// </summary>
        public float PhaseGrowth(PhysicsPhase phase) => _phaseGrowth[(int)phase];

        public ArticulationHandle PhaseArticulation(PhysicsPhase phase) => _phaseArticulation[(int)phase];

        /// <summary>
        /// Сон (7з-3): пробуждений спящих сочленений по причине; засыпаний; касаний спящего тела неспящим телом на физике в подшагах (спящее —
        /// неподвижная опора); наибольшее число спящих на конец шага (у последнего тика — сейчас).
        /// </summary>
        public int Wakes(WakeReason reason) => _wakes[(int)reason];

        /// <summary>Пробуждений всего (все причины).</summary>
        public int WakesTotal
        {
            get
            {
                int n = 0;
                foreach (int w in _wakes) n += w;
                return n;
            }
        }

        public int Sleeps { get; private set; }
        public int SleepContacts { get; private set; }
        public int AsleepMax { get; private set; }

        /// <summary>Событий (тело·тиков быстрее порога) и самые быстрые из них (по убыванию скорости, до <see cref="TopCapacity"/>).</summary>
        public int Events { get; private set; }
        public int TopCount => _topCount;

        public Event Top(int k)
        {
            if ((uint)k >= (uint)_topCount) throw new ArgumentOutOfRangeException(nameof(k), k, $"самых быстрых {_topCount}");
            return _top[k];
        }

        public void Clear()
        {
            Array.Clear(_sources);
            Array.Clear(_speedHistogram);
            Array.Clear(_angularHistogram);
            Array.Clear(_top);
            _topCount = 0;
            Steps = Substeps = 0;
            Seconds = 0;
            SpeedMax = AngularSpeedMax = 0;
            SpeedBody = BodyHandle.None;
            Capped = NonFinite = 0;
            SpeedSamples = 0;
            PairShare = PassShare = PushPopSpeed = 0;
            PairSubsteps = 0;
            ReactionMax = ReactionSum = 0;
            ReactionArticulation = ArticulationHandle.None;
            Hits = HitsCapped = HitsBudgeted = 0;
            HitDvShare = HitDv = HitDw = 0;
            FrictionContacts = FrictionHeld = 0;
            EnergyGrowth = EnergyGrowthTotal = EnergyGrowthPushed = 0;
            EnergyArticulation = ArticulationHandle.None;
            EnergyGrowthPaired = EnergyGrowthPairedTotal = 0;
            EnergyPairedArticulation = ArticulationHandle.None;
            Array.Clear(_phaseGrowth);
            Array.Clear(_phaseArticulation);
            Array.Clear(_wakes);
            Sleeps = SleepContacts = AsleepMax = 0;
            Events = 0;
        }

        internal void Wake(WakeReason reason) => _wakes[(int)reason]++;

        internal void Slept() => Sleeps++;

        internal void SleepContact() => SleepContacts++;

        internal void AsleepAt(int count) => AsleepMax = Math.Max(AsleepMax, count);

        internal void Correction(int i, float lin, float ang, float hid)
        {
            ref var s = ref _sources[i];
            s.Count++;
            s.Linear = MathF.Max(s.Linear, lin);
            s.Angular = MathF.Max(s.Angular, ang);
            s.Hidden = MathF.Max(s.Hidden, hid);
        }

        internal void ImpulseTick(int i, float j)
        {
            ref var s = ref _sources[i];
            s.ImpulseTickMax = MathF.Max(s.ImpulseTickMax, j);
            s.ImpulseSum += j;
        }

        internal void Contact(int i, float depth, float solved)
        {
            ref var s = ref _sources[i];
            s.Contacts++;
            s.Depth = MathF.Max(s.Depth, depth);
            s.Deferred = MathF.Max(s.Deferred, depth - solved);
        }

        internal void VelocitySolved(int i, float v)
        {
            ref var s = ref _sources[i];
            s.VelocitySolved = MathF.Max(s.VelocitySolved, v);
        }

        internal void Push(int i, float impulse, float pop, float passShare)
        {
            ref var s = ref _sources[i];
            s.Applied += impulse;
            PushPopSpeed = MathF.Max(PushPopSpeed, pop);
            if (passShare >= 0) PassShare = MathF.Max(PassShare, passShare);
        }

        internal void Budget(float share, int pairs)
        {
            PairShare = MathF.Max(PairShare, share);
            PairSubsteps += pairs;
        }

        internal void PassSpent(float share) => PassShare = MathF.Max(PassShare, share);

        internal void Hit(float dvShare, bool capped, bool budgeted, float dv, float dw)
        {
            Hits++;
            if (capped) HitsCapped++;
            if (budgeted) HitsBudgeted++;
            HitDvShare = MathF.Max(HitDvShare, dvShare);
            HitDv = MathF.Max(HitDv, dv);
            HitDw = MathF.Max(HitDw, dw);
        }

        internal void Reaction(ArticulationHandle articulation, float j)
        {
            ReactionSum += j;
            if (!(j > ReactionMax)) return;
            ReactionMax = j;
            ReactionArticulation = articulation;
        }

        internal void Friction(bool held)
        {
            FrictionContacts++;
            if (held) FrictionHeld++;
        }

        internal void Speed(BodyHandle body, float v, float w, bool capped)
        {
            SpeedSamples++;
            if (!float.IsFinite(v) || !float.IsFinite(w)) NonFinite++;
            if (capped) Capped++;
            _speedHistogram[Bin(v, SpeedBin, SpeedBins)]++;
            _angularHistogram[Bin(w, AngularBin, AngularBins)]++;
            if (v > SpeedMax)
            {
                SpeedMax = v;
                SpeedBody = body;
            }
            // как у линейной: нечисловая скорость — в NonFinite, наибольшую не портит (MathF.Max тянет NaN)
            if (w > AngularSpeedMax) AngularSpeedMax = w;
        }

        internal void Energy(ArticulationHandle articulation, float growth, bool pushed, bool paired)
        {
            if (pushed)
            {
                EnergyGrowthPushed = MathF.Max(EnergyGrowthPushed, growth);
                return;
            }
            if (paired)
            {
                if (!(growth > EnergyGrowthPaired)) return;
                EnergyGrowthPaired = growth;
                EnergyPairedArticulation = articulation;
                return;
            }
            if (!(growth > EnergyGrowth)) return;
            EnergyGrowth = growth;
            EnergyArticulation = articulation;
        }

        internal void EnergyTotal(float growth) => EnergyGrowthTotal = MathF.Max(EnergyGrowthTotal, growth);

        internal void EnergyPairedTotal(float growth) => EnergyGrowthPairedTotal = MathF.Max(EnergyGrowthPairedTotal, growth);

        internal void Phase(int phase, float growth, ArticulationHandle articulation)
        {
            if (!(growth > _phaseGrowth[phase])) return;
            _phaseGrowth[phase] = growth;
            _phaseArticulation[phase] = articulation;
        }

        /// <summary>Событие: в счёт и в «самые быстрые» (фиксированный массив по убыванию скорости; равные — в порядке прихода).</summary>
        internal void AddEvent(in Event e)
        {
            Events++;
            if (_topCount == TopCapacity && !(e.Speed > _top[TopCapacity - 1].Speed)) return;
            int k = _topCount < TopCapacity ? _topCount++ : TopCapacity - 1;
            while (k > 0 && _top[k - 1].Speed < e.Speed)
            {
                _top[k] = _top[k - 1];
                k--;
            }
            _top[k] = e;
        }

        internal void Substep() => Substeps++;

        internal void Step(float dt)
        {
            Steps++;
            Seconds += dt;
        }

        private static int Bin(float v, float bin, int bins) => v >= 0 ? (int)MathF.Min(v / bin, bins - 1) : bins - 1;

        private static float Percentile(long[] histogram, float bin, float q)
        {
            long total = 0;
            foreach (long c in histogram) total += c;
            if (total == 0) return 0;
            long need = (long)Math.Ceiling(total * (double)q), acc = 0;
            for (int i = 0; i < histogram.Length; i++)
                if ((acc += histogram[i]) >= need) return (i + 1) * bin;
            return histogram.Length * bin;
        }

        /// <summary>Сводка строками с приставкой <paramref name="prefix"/> (для агента и журнала; выделяет память).</summary>
        public string Summary(string prefix)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine(inv, $"{prefix} шагов {Steps} ({Seconds:F2} с), подшагов {Substeps}; событий быстрее порога {Events}");
            sb.AppendLine(inv, $"{prefix} скорости тел на физике: v {SpeedMax:F2} м/с (тело {SpeedBody}), ω {AngularSpeedMax:F1} рад/с; " +
                               $"на потолке профиля {Capped} раз; нечисловых {NonFinite} из {SpeedSamples}");
            sb.AppendLine(inv, $"{prefix} по подшагам: v p99 {SpeedPercentile(0.99f):F2} / p99,9 {SpeedPercentile(0.999f):F2} / " +
                               $"p99,99 {SpeedPercentile(0.9999f):F2} м/с; ω p99 {AngularPercentile(0.99f):F0} / p99,9 {AngularPercentile(0.999f):F0} / " +
                               $"p99,99 {AngularPercentile(0.9999f):F0} рад/с");
            sb.Append(inv, $"{prefix} поправки (макс за подшаг м/с | рад/с | скрыто м/с | псевдоимпульс за тик макс / сумма, Н·с | поправок | " +
                           $"контактов | глубина макс / отложено, м | погашено скоростью, м/с | толчок Σλ/h, Н·с):");
            for (int i = 0; i < SourceCount; i++)
            {
                ref readonly var s = ref _sources[i];
                if (s.Count == 0 && s.Contacts == 0 && s.VelocitySolved == 0 && s.Applied == 0) continue;
                sb.AppendLine();
                sb.Append(inv, $"{prefix}   {SourceNames[i],-15} {s.Linear,7:F2} | {s.Angular,6:F1} | {s.Hidden,5:F2} | {s.ImpulseTickMax,7:F2} / " +
                               $"{s.ImpulseSum,8:F2} | {s.Count} | {s.Contacts} | {s.Depth:F4} / {s.Deferred:F4} | {s.VelocitySolved:F2} | {s.Applied:F2}");
            }
            sb.AppendLine();
            sb.AppendLine(inv, $"{prefix} толчки живых: пар·подшагов {PairSubsteps}; доля бюджета пары {PairShare:F3}, прохода {PassShare:F3}; " +
                               $"выталкивание сверх толкающего {PushPopSpeed:F2} м/с; отдача макс {ReactionMax:F2} Н·с (сочленение {ReactionArticulation}), " +
                               $"сумма {ReactionSum:F2} Н·с");
            sb.AppendLine(inv, $"{prefix} удары: {Hits} (урезано потолком Δv {HitsCapped}, проходом {HitsBudgeted}); доля Δv {HitDvShare:F3}; " +
                               $"Δv тела {HitDv:F2} м/с, Δω {HitDw:F1} рад/с");
            sb.AppendLine(inv, $"{prefix} трение покоя о землю: держало {FrictionHeld} из {FrictionContacts}");
            sb.AppendLine(inv, $"{prefix} рост энергии за подшаг: сочленение без толчков и контакта с телом на физике {EnergyGrowth:F4} Дж " +
                               $"({EnergyArticulation}), в контакте {EnergyGrowthPaired:F4} Дж ({EnergyPairedArticulation}), все в контакте вместе " +
                               $"{EnergyGrowthPairedTotal:F4} Дж, все без толчков {EnergyGrowthTotal:F4} Дж, толкаемое живыми {EnergyGrowthPushed:F4} Дж");
            sb.AppendLine(inv, $"{prefix} сон: засыпаний {Sleeps}, спящих до {AsleepMax}; касаний спящих (опора) {SleepContacts}; пробуждений {WakesTotal}:" +
                               $" толчок {Wakes(WakeReason.Push)}, удар {Wakes(WakeReason.Hit)}, касание {Wakes(WakeReason.Touch)}, цепочка " +
                               $"{Wakes(WakeReason.Chain)}, команда {Wakes(WakeReason.Command)}, настройки {Wakes(WakeReason.Settings)}");
            sb.Append(inv, $"{prefix} рост энергии по фазам подшага, Дж:");
            for (int p = 0; p < PhaseCount; p++) sb.Append(inv, $" {PhaseNames[p]} {_phaseGrowth[p]:F4};");
            for (int k = 0; k < _topCount; k++)
            {
                sb.AppendLine();
                sb.Append(prefix).Append(" топ: ").Append(_top[k].ToString());
            }
            return sb.ToString();
        }
    }
}
