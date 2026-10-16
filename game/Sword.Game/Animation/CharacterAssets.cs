using Sword.Engine.Animation;
using Sword.Engine.Content;
using Sword.Engine.Diagnostics;
using Sword.Game.Combat;
using Sword.Game.Data;

namespace Sword.Game.Animation;

/// <summary>
/// Ассеты бойца: скелет Manny, поза привязки, клипы, таблицы действий, оружия (<see cref="WeaponTable"/>) и ударов
/// (<see cref="StrikeTable"/>). Без любого из них игра не стартует. Всё неизменяемо — общее для симуляций процесса.
/// </summary>
public sealed class CharacterAssets
{
    /// <summary>Скелет персонажа в библиотеке контента.</summary>
    public const string SkeletonId = "ue5_manny";

    /// <summary>Меш персонажа (печёт конвертер character по рецепту Bake/recipes/src/_Export/meshes/manny.glb.bake.json) — из него поза привязки.</summary>
    public const string MeshFile = "meshes/characters/manny.swmesh";

    /// <summary>Сустав, в котором оружие: меч — жёсткое вложение в его систему.</summary>
    public const string HandJoint = "hand_r";

    /// <summary>Библиотека клипов скелета — только чтение; пул поз у каждой симуляции свой (<see cref="GameCore"/>).</summary>
    public ClipLibrary Clips { get; }
    public ActionTable Actions { get; }

    /// <summary>Мечи пака по алфавиту.</summary>
    public WeaponTable Weapons { get; }

    /// <summary>Удары slash1–4, stab1–3: окна по клинку меча по умолчанию.</summary>
    public StrikeTable Strikes { get; }

    /// <summary>Номер сустава <see cref="HandJoint"/> в скелете.</summary>
    public int Hand { get; }

    private Ragdoll.CorpseJoints.Anatomy? _hinges;

    /// <summary>
    /// Анатомические оси шарниров трупа (<see cref="Ragdoll.CorpseJoints.Anatomy"/>) — считаются один раз на ассеты (десятки мс).
    /// </summary>
    public Ragdoll.CorpseJoints.Anatomy Hinges =>
        LazyInitializer.EnsureInitialized(ref _hinges, () => new Ragdoll.CorpseJoints.Anatomy(Clips, new Bodies.BodyJoints(Clips.Skeleton)));

    private CharacterAssets(ClipLibrary clips, ActionTable actions, WeaponTable weapons, StrikeTable strikes, int hand)
    {
        Clips = clips;
        Actions = actions;
        Weapons = weapons;
        Strikes = strikes;
        Hand = hand;
    }

    /// <summary>Из пака игры (<see cref="ContentLocator"/>).</summary>
    /// <exception cref="FileNotFoundException">Нет пака или нужного в нём.</exception>
    /// <exception cref="InvalidDataException">Пак или ассеты испорчены.</exception>
    public static CharacterAssets LoadFromPak()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        using var pak = ContentLocator.Open();
        var assets = Load(pak);
        Log.Info("anim", $"ассеты бойца загружены за {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} мс");
        return assets;
    }

    /// <exception cref="FileNotFoundException">Нет скелета, меша, ни одного клипа скелета или ни одного меча.</exception>
    /// <exception cref="InvalidDataException">
    /// Скелет, меш, меш меча или actions.json испорчены; скелет меша другой; в скелете нет кисти <see cref="HandJoint"/>.
    /// </exception>
    public static CharacterAssets Load(IContentSource content)
    {
        string skeletonPath = ClipLibrary.SkeletonFile(SkeletonId);
        if (!content.Exists(skeletonPath)) throw new FileNotFoundException($"нет скелета {skeletonPath} в {content.Location}", skeletonPath);
        if (!content.Exists(MeshFile)) throw new FileNotFoundException($"нет меша персонажа {MeshFile} в {content.Location}", MeshFile);

        Skeleton skeleton;
        using (var s = content.OpenRead(skeletonPath)) skeleton = Skeleton.Read(s, content.Describe(skeletonPath));
        using (var s = content.OpenRead(MeshFile)) skeleton = skeleton.WithMeshBindPose(s, content.Describe(MeshFile));

        var skipped = new List<string>();
        var files = content.List(ClipLibrary.AnimDir, ClipLibrary.ClipExt)
            .Select(p => new ClipSource(p, () => content.OpenRead(p), content.Describe(p)));
        var clips = ClipLibrary.Load(skeleton, files, skipped)
            ?? throw new FileNotFoundException($"в {content.Location} нет клипов скелета {SkeletonId} " +
                $"({ClipLibrary.AnimDir}/<набор>/*{ClipLibrary.ClipExt})", ClipLibrary.AnimDir);
        foreach (string s in skipped) Log.Warning("anim", s);
        Log.Info("anim", $"библиотека {content.Location} — {clips.ClipCount} клипов, источников " +
            $"{clips.Clips.ToArray().Select(c => c.Source).Distinct().Count()}, {clips.SampleRate} Гц, суставов {skeleton.JointCount}");

        string? json = null;
        if (content.Exists(ActionTable.ActionsFile))
        {
            using var reader = new StreamReader(content.OpenRead(ActionTable.ActionsFile));
            json = reader.ReadToEnd();
        }
        var warnings = new List<string>();
        var actions = ActionTable.Resolve(clips.FindClip, c => clips.Clips[c].Name, json, warnings);
        foreach (string w in warnings) Log.Warning("anim", w);

        var weapons = WeaponTable.Load(content);
        int hand = skeleton.Find(HandJoint);
        if (hand < 0) throw new InvalidDataException($"в скелете {SkeletonId} нет сустава «{HandJoint}» — в нём оружие");
        var strikes = StrikeTable.Analyse(clips, actions, hand, weapons.Weapons[weapons.Default].BladeTip);
        Log.Info("combat", $"оружие: {weapons.Names}; удары по клинку {weapons.Weapons[weapons.Default].Name} — {strikes.Describe(clips)}");
        return new CharacterAssets(clips, actions, weapons, strikes, hand);
    }
}
