using System.Globalization;
using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;
using Sword.Engine.Runtime;

namespace Sword.Engine.Client;

/// <summary>
/// Ручки симуляции в группе панели F3. Чтение — из ресурса мира или ожидающее значение (команда поставлена, тик ещё не
/// сыгран — ползунок не отскакивает). Запись — <see cref="TuningCommand"/> на ближайший тик, как ввод игрока (в реплей).
/// Ключ строки — ключ секции конфига; кадр без выделений.
/// </summary>
public sealed class TuningBinding
{
    private sealed class Slot(TuningKnob knob)
    {
        public readonly TuningKnob Knob = knob;
        public bool Pending;
        public float Value;
        public ulong Tick;
    }

    private readonly SimulationLink _link;
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);

    public TuningBinding(SimulationLink link) => _link = link ?? throw new ArgumentNullException(nameof(link));

    /// <summary>Ручка симуляции по полному имени «секция.ключ»; нет — <see cref="ArgumentException"/> с перечнем.</summary>
    public TuningKnob Knob(string name) => SlotOf(name).Knob;

    /// <summary>Значение ручки для панели: ожидающее (команда поставлена, её тик не сыгран) или в мире.</summary>
    public float Value(string name) => Value(SlotOf(name));

    /// <summary>Поставлена ли команда ручки, чей тик ещё не сыгран.</summary>
    public bool IsPending(string name)
    {
        var slot = SlotOf(name);
        return slot.Pending && _link.Tick <= slot.Tick;
    }

    /// <summary>Новое значение — команда на ближайший тик (прижато к диапазону, нечисло — ничего); то же, что показано, —
    /// без команды.</summary>
    public void Set(string name, float value) => Set(SlotOf(name), value);

    /// <summary>Строка ручки <paramref name="name"/> в группе: число — ползунок и поле, флаг — переключатель.</summary>
    public TuningGroup Add(TuningGroup group, string name)
    {
        var slot = SlotOf(name);
        var k = slot.Knob;
        return k.Kind == TuningKind.Bool
            ? group.Bool(k.Key, k.Label, () => Value(slot) != 0, v => Set(slot, v ? 1 : 0), k.Hint, k.Default != 0)
            : group.Float(k.Key, k.Label, () => Value(slot), v => Set(slot, v), k.Min, k.Max, k.Log, k.Format, k.Hint, k.Default);
    }

    /// <summary>Все ручки секции <paramref name="section"/> по порядку регистрации.</summary>
    public TuningGroup AddSection(TuningGroup group, string section)
    {
        foreach (var k in _link.Tuning)
            if (string.Equals(k.Section, section, StringComparison.OrdinalIgnoreCase)) Add(group, k.Name);
        return group;
    }

    /// <summary>
    /// «Сохранить» F3: ручки группы → секция <c>Id</c> файла <paramref name="path"/> (<see cref="UserConfig.WriteSection"/>,
    /// другие секции остаются). Значение по умолчанию убирает ключ — файл хранит только отличия. Группа без ручек симуляции
    /// файл не трогает: секция без потребителя сделала бы следующий запуск ошибкой «неизвестная секция».
    /// </summary>
    public string Save(TuningGroup group, string path)
    {
        if (!HasKnobs(group)) return ClientOnly(group, path);
        var changes = new List<(string Key, JsonNode? Value)>();
        int set = 0;
        foreach (var slot in SlotsOf(group))
        {
            var k = slot.Knob;
            float v = Value(slot);
            if (v == k.Default)
            {
                changes.Add((k.Key, null));
                continue;
            }
            changes.Add((k.Key, k.Kind == TuningKind.Bool ? JsonValue.Create(v != 0)
                : JsonValue.Create(double.Parse(v.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture))));
            set++;
        }
        UserConfig.WriteSection(path, group.Id, changes);
        return set == 0 ? $"сохранено: всё по умолчанию ({Path.GetFileName(path)})" : $"сохранено значений: {set} ({Path.GetFileName(path)})";
    }

    /// <summary>«Загрузить» F3: секция группы из <paramref name="path"/> — командами ручек; нет ключа — значение по
    /// умолчанию, не числа и незнакомые ключи пропускаются. Итог — строка состояния.</summary>
    public string Load(TuningGroup group, string path)
    {
        if (!HasKnobs(group)) return ClientOnly(group, path);
        var values = UserConfig.Numbers(UserConfig.ReadSection(path, group.Id));
        int loaded = 0;
        foreach (var slot in SlotsOf(group))
        {
            bool has = values.TryGetValue(slot.Knob.Key, out double v) && double.IsFinite(v);
            Set(slot, has ? (float)v : slot.Knob.Default);
            if (has) loaded++;
        }
        return $"загружено значений: {loaded} ({Path.GetFileName(path)}), остальные — по умолчанию";
    }

    /// <summary>Есть ли в группе ручки симуляции (<see cref="SlotsOf"/>).</summary>
    public bool HasKnobs(TuningGroup group) => SlotsOf(group).Any();

    private static string ClientOnly(TuningGroup group, string path) =>
        $"«{group.Title}» — ручки клиента: только до выхода, в {Path.GetFileName(path)} не пишутся";

    /// <summary>Ручки симуляции строк группы: ключ строки — ключ секции <c>Id</c> группы; строки без ручки пропускаются.</summary>
    private IEnumerable<Slot> SlotsOf(TuningGroup group)
    {
        foreach (var item in group.Items)
        {
            string? key = item switch { TuningFloat f => f.Key, TuningBool b => b.Key, _ => null };
            if (string.IsNullOrEmpty(key)) continue;
            string name = group.Id + "." + key;
            if (_slots.TryGetValue(name, out var slot)) yield return slot;
            else if (_link.Tuning.Any(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase))) yield return SlotOf(name);
        }
    }

    private float Value(Slot slot)
    {
        if (slot.Pending)
        {
            if (_link.Tick <= slot.Tick) return slot.Value;
            slot.Pending = false;   // тик команды сыгран: правда — в мире (и если команду перебили — тоже)
        }
        return slot.Knob.Read(_link.World);
    }

    private void Set(Slot slot, float value)
    {
        float v = slot.Knob.Clamp(value);
        if (float.IsNaN(v) || v == Value(slot)) return;
        _link.Enqueue(new TuningCommand { Knob = slot.Knob.Id, Value = v });
        slot.Pending = true;
        slot.Value = v;
        slot.Tick = _link.Tick;   // между тиками команда — на тик Tick (ближайший)
    }

    /// <summary>Строка ручки по имени: найденная — без выделений (кадр панели), новая — в <see cref="AddSlot"/>.</summary>
    private Slot SlotOf(string name) => _slots.TryGetValue(name, out var slot) ? slot : AddSlot(name);

    // Отдельный метод: лямбда, захватившая параметр, заводит замыкание при входе в метод — на каждый вызов, а не только в этой ветке.
    private Slot AddSlot(string name)
    {
        var knob = _link.Tuning.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"нет ручки «{name}»; есть: {string.Join(", ", _link.Tuning.Select(k => k.Name))}", nameof(name));
        var slot = new Slot(knob);
        _slots[name] = slot;
        return slot;
    }
}
