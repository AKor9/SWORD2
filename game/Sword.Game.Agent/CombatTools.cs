using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Engine.Agent;
using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Game.Contracts;

namespace Sword.Game.Agent;

/// <summary>
/// Инструмент боя (этап 6б): <c>combat.weapons</c> — оружие и удары словами через вид <see cref="ICombatView"/>: таблица мечей
/// (номер из <see cref="WeaponSlot"/> → имя, клинок), инвентарь игрока, меч и клинок каждого бойца (отрезок тика и прошлого,
/// пройденное кончиком за тик) и таблица ударов (слоты, клипы, окна урона). Номера и числа компонентов видны и в
/// <c>entity.get</c>, но без имён мечей и клипов.
/// </summary>
public static class CombatTools
{
    public static void Register(AgentHost host)
    {
        host.Add(new AgentTool
        {
            Name = "combat.weapons", Title = "Оружие и удары",
            Description = "Оружие после последнего тика: таблица мечей пака (номер = WeaponSlot.Index, имя, меш, клинок в системе " +
                "кисти hand_r — основание и кончик, длина), меч по умолчанию (им размечены удары), инвентарь игрока (доступное, " +
                "выбранное), бойцы — меч в руке и клинок в мире (основание и кончик тика и прошлого тика, путь кончика за тик, м); " +
                "attacks — таблица ударов: слот, клип, клип отбитого удара, длительность, укол, слой верха, окна урона (начало, " +
                "конец, пик и его скорость, направление замаха). entity — только этот боец.",
            Arguments = new JsonObject
            {
                ["entity"] = new JsonObject { ["type"] = "string", ["description"] = "номер «12v3» или имя (по умолчанию — все бойцы)" },
                ["attacks"] = new JsonObject { ["type"] = "boolean", ["description"] = "таблица ударов (по умолчанию true)" },
            },
            Run = async call =>
            {
                string? which = call.String("entity");
                bool attacks = call.Node("attacks")?.GetValue<bool>() ?? true;
                var result = await host.Serve(ctx => (Data: Weapons(ctx, host.Names, which, attacks), ctx.Point), call.Cancel);
                return new ToolOutput(result.Data) { Point = result.Point };
            },
        });
    }

    private static JsonObject Weapons(in ServiceContext ctx, IEntityNames? names, string? which, bool attacks)
    {
        var world = ctx.World;
        var view = world.View<ICombatView>();
        var weapons = view.Weapons.ToArray();
        string Name(int w) => (uint)w < (uint)weapons.Length ? weapons[w].Name : $"?{w}";

        var table = new JsonArray();
        for (int w = 0; w < weapons.Length; w++)
            table.Add(new JsonObject
            {
                ["index"] = w, ["name"] = weapons[w].Name, ["mesh"] = weapons[w].Mesh,
                ["bladeBase"] = Vec(weapons[w].BladeBase), ["bladeTip"] = Vec(weapons[w].BladeTip), ["length"] = Round(weapons[w].Length),
            });
        var inventory = world.Resource<PlayerInventory>();
        var owned = new JsonArray();
        for (int w = 0; w < PlayerInventory.MaxWeapons; w++)
            if (inventory.Has(w)) owned.Add(Name(w));

        Entity? only = null;
        if (which is not null)
        {
            var e = which.Length > 0 && char.IsDigit(which[0]) ? FieldCodec.ParseEntity(which)
                : names?.Resolve(ctx, which) ?? throw new ArgumentException($"нет сущности с именем «{which}» (или номер «12v3»)");
            if (!world.IsAlive(e) || !world.Has<WeaponSlot>(e)) throw new ArgumentException($"у {which} нет меча (WeaponSlot)");
            only = e;
        }
        var fighters = new JsonArray();
        foreach (var chunk in world.Iterate(world.Query().With<Fighter>().Read<WeaponSlot>().Read<Blade>()))
        {
            var slots = chunk.Read<WeaponSlot>();
            var blades = chunk.Read<Blade>();
            for (int i = 0; i < chunk.Count; i++)
            {
                var e = chunk.Entities[i];
                if (only is { } o && o != e) continue;
                var b = blades[i];
                fighters.Add(new JsonObject
                {
                    ["entity"] = e.ToString(), ["name"] = names?.NameOf(ctx, e),
                    ["weapon"] = Name(slots[i].Index), ["weaponIndex"] = slots[i].Index,
                    ["bladeWeapon"] = b.Weapon < 0 ? null : Name(b.Weapon),
                    ["base"] = Vec(b.Base), ["tip"] = Vec(b.Tip), ["prevBase"] = Vec(b.PrevBase), ["prevTip"] = Vec(b.PrevTip),
                    ["tipTravel"] = Round(Vector3.Distance(b.Tip, b.PrevTip)),
                });
            }
        }

        var data = new JsonObject
        {
            ["weapons"] = table,
            ["defaultWeapon"] = Name(view.DefaultWeapon),
            ["inventory"] = new JsonObject { ["owned"] = owned, ["equipped"] = Name(inventory.Equipped), ["equippedIndex"] = inventory.Equipped },
            ["fighters"] = fighters,
        };
        if (!attacks) return data;
        world.TryView<IAnimationView>(out var clips);
        string Clip(int c) => c < 0 ? "—" : clips is not null && c < clips.ClipCount ? clips.ClipName(c) : c.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var list = new JsonArray();
        foreach (var a in view.Attacks)
        {
            var windows = new JsonArray();
            foreach (var w in a.Windows)
                windows.Add(new JsonObject
                {
                    ["start"] = Round(w.Start), ["end"] = Round(w.End), ["peakTime"] = Round(w.PeakTime), ["peakSpeed"] = Round(w.PeakSpeed),
                    ["swingDir"] = new JsonArray(Round(w.SwingDir.X), Round(w.SwingDir.Y)),
                });
            list.Add(new JsonObject
            {
                ["slot"] = a.Slot, ["clip"] = Clip(a.Clip), ["blockedClip"] = a.BlockedClip < 0 ? null : Clip(a.BlockedClip),
                ["duration"] = Round(a.Duration), ["thrust"] = a.Thrust, ["upper"] = a.Upper, ["windows"] = windows,
            });
        }
        data["attacks"] = list;
        return data;
    }

    private static JsonArray Vec(Vector3 v) => [Round(v.X), Round(v.Y), Round(v.Z)];

    private static double Round(float x) => Math.Round(x, 4);
}
