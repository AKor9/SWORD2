using Sword.Engine.Client;
using Sword.Game.Contracts;

namespace Sword.Game.Cutting;

/// <summary>
/// Модуль Cutting (<c>cutting</c>), часть клиента: по записям отрубов (<see cref="ISeverFeed"/>) режет меш бойца в фоне
/// (<see cref="SkinCuts"/>) и отдаёт части клиенту арены (<see cref="FighterSkinParts"/>); крышки красит шейдер скина по UV крышки.
/// Активна, только когда активна часть симуляции (тот же Id). Нет сервисов — ничего не делает.
/// </summary>
public sealed class CuttingClientModule : IClientModule
{
    public string Id => CuttingModule.ModuleId;

    public void ConfigureClient(ClientBuilder b)
    {
        if (b.Service<FighterSkinParts>() is not { } parts || b.Service<IFighterPoses>() is not { } poses || b.Service<ISkinMesh>() is not { } skin
            || b.Service<ISeverFeed>() is not { } feed)
            return;
        var cuts = new SkinCuts(parts, poses, skin, feed);
        b.Initialize(ctx => cuts.Init(ctx.Renderer))
            .AfterTick(cuts.AfterTick)
            .Extract((in FrameView _) => cuts.Update())
            .Report(() => new { cuts = cuts.Cuts, meshes = cuts.Meshes, megabytes = cuts.Bytes / (1024.0 * 1024.0), lastMs = cuts.LastMs });
    }
}
