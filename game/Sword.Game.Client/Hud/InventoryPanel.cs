using Sword.Engine.Client;
using Sword.Engine.Ui;
using Sword.Game.Contracts;

namespace Sword.Game.Client;

/// <summary>
/// Панель инвентаря (I; <c>InventoryUi</c> SWORD 2eab13b, точка 6е) — панель клиента (<see cref="IClientPanel"/>): её ведёт слой
/// панелей клиента движка до ввода игры, поэтому щелчок по панели в бой не уходит («мышь над панелью не бьёт»). Открыта — справа
/// по центру высоты: заголовок «ИНВЕНТАРЬ   оружие N», строка на меч (номер 1–9, имя; выбранный — зелёная точка и яркое имя,
/// строка подсвечена), внизу — подсказка. Щелчок по строке — «взять меч» (<see cref="TakeClicked"/>: команду арены
/// <see cref="ArenaAction.EquipWeapon"/> ставит клиент арены на ближайший тик); цифры 1–9 при открытой панели — тоже (ведёт
/// клиент арены по фронтам тика). Закрытая панель не рисуется и не стоит ничего: подпись меча в углу рисует HUD.
///
/// Открывает и закрывает её клиент арены (действие <see cref="ArenaInput.Inventory"/> — ввод игры: поле ввода другой панели с
/// фокусом его забирает). Кадр без выделений: подписи номеров, заголовок и подсказка — строки заранее или на стеке, ID строк —
/// стек ID.
/// </summary>
public sealed class InventoryPanel : IClientPanel
{
    private static readonly string[] Digits = ["1", "2", "3", "4", "5", "6", "7", "8", "9"];

    private SimulationLink? _link;
    private string[] _names = [];
    private int _clicked = -1;
    private string _footer = "1–9, щелчок — взять    I — закрыть";

    /// <summary>Панель открыта (I).</summary>
    public bool Open { get; set; }

    /// <summary>Прямоугольник панели в последнем кадре, пиксели (для тестов и снимков); закрыта — пусто.</summary>
    public UiRect LastRect { get; private set; }

    /// <summary>Симуляция и имена мечей таблицы (по номеру) — клиент арены после <c>Initialize</c>.</summary>
    public void Attach(SimulationLink link, string[] names)
    {
        _link = link;
        _names = names;
    }

    /// <summary>Подсказка внизу панели — по действующим привязкам (цифры и клавиша инвентаря).</summary>
    public void UseKeys(string first, string last, string toggle) => _footer = $"{first}–{last}, щелчок — взять    {toggle} — закрыть";

    public bool Update(in FrameInput input) => Open && _link is not null;

    /// <summary>
    /// Меч, выбранный щелчком с прошлого тика (номер таблицы), — забирает первый тик (клиент арены, <c>BeforeTick</c>); не было — −1.
    /// </summary>
    public int TakeClicked()
    {
        int weapon = _clicked;
        _clicked = -1;
        return weapon;
    }

    /// <summary>Кадр открытой панели (<c>InventoryUi.Draw</c> SWORD). Истина — курсор над панелью: мышь не уходит в бой.</summary>
    public bool Draw(UiContext ui)
    {
        LastRect = default;
        if (!Open || _link is null) return false;
        var t = ui.Theme;
        var inventory = _link.World.Resource<PlayerInventory>();
        int count = inventory.Count;
        float rowH = ui.S(26);
        float w = ui.S(340), h = ui.S(22 + 12 + 24) + rowH * count;
        var screen = ui.Screen;
        var panel = new UiRect(screen.X1 - w - ui.S(16), screen.Centre.Y - h / 2, screen.X1 - ui.S(16), screen.Centre.Y + h / 2);
        LastRect = panel;
        ui.Panel(panel);
        var inner = panel.Shrink(ui.S(8));
        Span<char> buf = stackalloc char[48];
        ui.SectionHeader(inner.SplitTop(ui.S(22), out inner), new Line(buf).S("ИНВЕНТАРЬ   оружие ").I(count).Text);
        inner.SplitTop(ui.S(6), out inner);
        ui.PushId("inventory");
        for (int i = 0; i < count; i++)
        {
            int weapon = inventory.OwnedAt(i);
            var row = inner.SplitTop(rowH, out inner);
            bool selected = weapon == inventory.Equipped;
            if (ui.Selectable(row, ui.Id(weapon), selected)) _clicked = weapon;
            if (selected) ui.StatusDot(new(row.X0 + ui.S(12), row.Centre.Y), t.Good, 3);
            var key = row.SplitLeft(ui.S(44), out var name);
            ui.Label(key, i < Digits.Length ? Digits[i] : "", t.TextFaint, UiAlign.Right, UiFace.Num);
            ui.Label(name with { X0 = name.X0 + ui.S(12) }, (uint)weapon < (uint)_names.Length ? _names[weapon] : "?",
                selected ? t.Text : t.TextDim, UiAlign.Left, UiFace.Label);
        }
        ui.PopId();
        ui.Label(inner, _footer, t.TextFaint, UiAlign.Left, UiFace.Label, t.SmallFontSize);
        return ui.Input.MouseInside && panel.Contains(ui.Input.Mouse);
    }
}
