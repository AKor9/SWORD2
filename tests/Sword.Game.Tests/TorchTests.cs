using System.Numerics;
using Sword.Engine.Content;
using Sword.Engine.Ecs;
using Sword.Engine.Formats;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Arena;
using Sword.Game.Bootstrap;
using Sword.Game.Combat;
using Sword.Game.Contracts;
using Sword.Game.Data;

namespace Sword.Game.Tests;

/// <summary>
/// Оружие только игрока (<c>combat.playerOnlyWeapons</c>, по умолчанию факел): враги берут из остальных; факел последний по
/// алфавиту, поэтому выбор врагов тот же, что у пака без факела; у игрока он в инвентаре, T доходит до него и дальше к первому.
/// </summary>
public sealed class TorchTests
{
    private static WeaponInfo Info(string name) => new(name, $"meshes/weapons/{name}.swmesh", Vector3.UnitY * 0.22f, Vector3.UnitY);

    /// <summary>Пак из шести мечей и факела — по алфавиту, как список контента.</summary>
    private static readonly WeaponTable Seven = new(
        [Info("combat_sword"), Info("katana"), Info("katana_v2"), Info("powerful_sword"), Info("sword_1h"), Info("sword_dss"), Info("torch")]);

    [Fact]
    public void EnemyWeapons_TableWithoutPlayerOnly_NamesAnyCase_AllPlayerOnlyIsError()
    {
        Assert.Equal([0, 1, 2, 3, 4, 5], Seven.EnemyWeapons(["torch"]));
        Assert.Equal([0, 1, 2, 3, 4, 5], Seven.EnemyWeapons(["TORCH", "axe"]));      // регистр не важен, имени нет в паке — исключать нечего
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], Seven.EnemyWeapons([]));
        Assert.Equal([0, 2, 3, 5], Seven.EnemyWeapons(["katana", "sword_1h", "torch"]));
        var one = new WeaponTable([Info("torch")]);
        Assert.Contains("combat.playerOnlyWeapons", Assert.Throws<InvalidDataException>(() => one.EnemyWeapons(["torch"])).Message);
    }

    /// <summary>
    /// Выбор врага по пулу без последнего оружия — тот же номер, что прежняя выборка по числу мечей пака без него (номер выборки меча —
    /// <see cref="ArenaRules.WeaponSampleBase"/> + партия · 33 + место), на разных зёрнах, тиках, партиях и местах; факела ни у одного
    /// врага за 4 · 10 · 32 = 1280 спаунов; корень, курс и зерно от пула не зависят.
    /// </summary>
    [Fact]
    public void EnemySpawn_PoolWithoutTorch_SameChoiceAsSixWeaponPak_NeverTorch()
    {
        int[] pool = Seven.EnemyWeapons(["torch"]);
        int spawns = 0;
        var seen = new HashSet<int>();
        foreach (ulong seed in new ulong[] { 1, 7, 12345, 0xDEADBEEF })
        {
            var rng = new SimRandom(seed);
            var center = new Vector3(seed % 5, 0, -(float)(seed % 3));
            for (uint batch = 0; batch < 10; batch++)
                for (int slot = 1; slot <= ArenaState.MaxEnemies; slot++)
                {
                    ulong tick = seed + batch * 97;
                    var s = ArenaRules.Enemy(rng, tick, batch, slot, center, pool);
                    uint sample = ArenaRules.WeaponSampleBase + batch * (uint)ArenaRules.MaxFighters + (uint)slot;
                    Assert.Equal(rng.Index(ArenaRules.SpawnStream, tick, Entity.Null, 6, sample), s.Weapon);
                    Assert.NotEqual(Seven.Find("torch"), s.Weapon);
                    var all = ArenaRules.Enemy(rng, tick, batch, slot, center, Seven.EnemyWeapons([]));
                    Assert.Equal((all.Position, all.Facing, all.Seed, all.AttackTimer), (s.Position, s.Facing, s.Seed, s.AttackTimer));
                    seen.Add(s.Weapon);
                    spawns++;
                }
        }
        Assert.Equal(1280, spawns);
        Assert.Equal([0, 1, 2, 3, 4, 5], seen.Order());
        Assert.Throws<ArgumentException>(() => ArenaRules.Enemy(new SimRandom(1), 0, 0, 1, Vector3.Zero, []));
    }

    /// <summary>
    /// В мире (синтетический пак: katana 0, sword_1h 1): оружие только игрока у врагов не бывает — ни при сборке мира, ни при постройке
    /// командой арены (прибавка, сброс), ни при возрождении; у игрока оно в инвентаре, T доходит до него и по кругу к первому; вид боя
    /// отдаёт оружие врагов.
    /// </summary>
    [Fact]
    public void PlayerOnly_NeverOnEnemies_InPlayerInventory_TCyclesThroughIt()
    {
        var sim = TestAssets.Simulation(ConfigSet.Empty(["arena.enemies=12", "combat.playerOnlyWeapons=[\"sword_1h\"]",
            "combat.playerWeapon=katana", "combat.enemiesAttack=false"]));
        Assert.Equal([0], sim.World.View<ICombatView>().EnemyWeapons.ToArray());
        void NoEnemyHasIt()
        {
            foreach (var chunk in sim.World.Iterate(sim.World.Query().With<EnemyTag>().Read<WeaponSlot>()))
                foreach (var w in chunk.Read<WeaponSlot>()[..chunk.Count])
                    Assert.Equal(0, w.Index);
        }
        NoEnemyHasIt();
        var inventory = sim.World.Resource<PlayerInventory>();
        Assert.Equal((0b11UL, 0), (inventory.Owned, inventory.Equipped));

        var player = GameCore.Player(sim.World);
        sim.Enqueue(new ArenaCommand { Action = ArenaAction.NextWeapon });
        sim.Enqueue(new ArenaCommand { Action = ArenaAction.SetEnemies, Enemies = 32 });
        sim.Step();
        Assert.Equal(1, sim.World.Get<WeaponSlot>(player).Index);   // T — к оружию только игрока
        NoEnemyHasIt();
        sim.Enqueue(new ArenaCommand { Action = ArenaAction.Reset });
        sim.Enqueue(new SceneCommand { Action = SceneAction.Kill, Slot = 3 });
        sim.Step();
        Assert.Equal(1, sim.World.Get<WeaponSlot>(player).Index);   // сброс — с мечом инвентаря
        NoEnemyHasIt();
        // Возрождение убитого врага (через 8 с) — тоже из оружия врагов.
        for (int t = 0; t < 8 * 120 + 60; t++) sim.Step();
        Assert.False(sim.World.Has<Dead>(RagdollCorpseTests.Fighter(sim, 3)));
        NoEnemyHasIt();
        sim.Enqueue(new ArenaCommand { Action = ArenaAction.NextWeapon });
        sim.Step();
        Assert.Equal(0, sim.World.Get<WeaponSlot>(player).Index);   // по кругу к первому
    }
}

/// <summary>
/// Факел пака игры (категория Content): в таблице оружия последний; оружие врагов — прежние шесть мечей эталонного пака, те же номера;
/// клинок по мешу (<see cref="WeaponMesh"/>): кончик — полюс шишки, на 2 см дальше её центра (шишка 5×5×4 см с центром на конце палки);
/// меш — две секции «wood» и «charred».
/// </summary>
[Trait("Category", "Content")]
public sealed class TorchContentTests
{
    [Fact]
    public void Torch_LastInTable_EnemiesKeepReferenceSwords_BladeTipIsKnobPole()
    {
        using var pak = PakSource.Open(TestContent.GamePak());
        var table = WeaponTable.Load(pak);
        Assert.Equal("torch", table.Weapons[^1].Name);
        int[] enemies = table.EnemyWeapons(new CombatSettings().PlayerOnlyWeapons);
        // оружие только игрока (факел, пробный молот) врагам не достаётся
        Assert.Equal(Enumerable.Range(0, table.Count).Where(w => table.Weapons[w].Name is not ("torch" or "maul")), enemies);

        Assert.Equal(["combat_sword", "katana", "katana_v2", "powerful_sword", "sword_1h", "sword_dss"], enemies.Select(w => table.Weapons[w].Name));

        var torch = table.Weapons[^1];
        SkinnedMeshData mesh;
        using (var s = pak.OpenRead(torch.Mesh)) mesh = SkinnedMeshData.Read(s, torch.Mesh);
        Assert.Equal(["wood", "charred"], mesh.Sections.Select(x => x.Material));
        var knob = mesh.Sections[1];
        var centre = Vector3.Zero;
        var used = new HashSet<uint>(mesh.Indices.AsSpan(knob.Start, knob.Count).ToArray());
        foreach (uint v in used) centre += mesh.Vertices[v].Position;
        centre /= used.Count;
        // Кончик — дальний край шишки: 2 см от её центра (полуось вдоль палки), дальше от кисти.
        Assert.InRange(Vector3.Distance(torch.BladeTip, centre), 0.017f, 0.023f);
        Assert.InRange(torch.BladeTip.Length() - centre.Length(), 0.015f, 0.021f);   // кисть сбоку от оси палки: полюс чуть в стороне
        // Палка 80 см от нижнего конца до центра шишки; хват — у нижнего конца: кисть ближе к нему, чем к шишке.
        var bottom = mesh.Vertices.Select(v => v.Position).MaxBy(p => Vector3.Distance(p, centre));
        Assert.InRange(Vector3.Distance(bottom, centre), 0.795f, 0.815f);
        Assert.InRange(bottom.Length(), 0.05f, 0.2f);
        Assert.InRange(torch.Length, 0.45f, 0.6f);   // клинок — от 22 см от кисти до кончика
    }
}
