using Sword.Engine.Animation;
using Sword.Engine.Content;
using Sword.Engine.Diagnostics;
using Sword.Game.Data;

namespace Sword.Game.Animation;

/// <summary>
/// Ассеты анимации бойца (<c>CharacterRig.TryLoad</c> SWORD, часть анимации): скелет Manny, поза привязки из его меша,
/// все клипы библиотеки этого скелета и таблица действий (<c>combat/actions.json</c>). Без любого из них игра не
/// стартует: боец SWORD2 — скелетный всегда (капсульных бойцов SWORD без рига здесь нет).
/// </summary>
public sealed class CharacterAssets
{
    /// <summary>Скелет персонажа в библиотеке контента.</summary>
    public const string SkeletonId = "ue5_manny";

    /// <summary>Меш персонажа (печёт skin.mesh по манифесту Bake/characters/manny/mesh.bake.json) — из него поза привязки.</summary>
    public const string MeshFile = "characters/manny/mesh.swmesh";

    /// <summary>Библиотека клипов скелета — только чтение; пул поз у каждой симуляции свой (<see cref="GameCore"/>).</summary>
    public ClipLibrary Clips { get; }
    public ActionTable Actions { get; }

    private CharacterAssets(ClipLibrary clips, ActionTable actions)
    {
        Clips = clips;
        Actions = actions;
    }

    /// <summary>Из пака игры (<see cref="ContentLocator"/>: переменная SWORD_CONTENT или ближайший пак вверх от exe).</summary>
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

    /// <exception cref="FileNotFoundException">Нет скелета, меша или ни одного клипа скелета.</exception>
    /// <exception cref="InvalidDataException">Скелет, меш или actions.json испорчены; скелет меша другой.</exception>
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
        return new CharacterAssets(clips, actions);
    }
}
