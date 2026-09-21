using System.Numerics;
using Sword.Engine.Rhi;
using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace Sword.Engine.Render;

/// <summary>
/// Кадр рендера v0 (точка 1а ARCHITECTURE_PLAN.md): очистить задний буфер и показать. На точке 1б здесь появится
/// рендер-граф v0 — проходы с объявленными ресурсами и барьерами; очистка станет его первым проходом.
/// </summary>
public sealed class FrameRenderer
{
    private readonly GraphicsDevice _device;

    public FrameRenderer(GraphicsDevice device) => _device = device;

    /// <summary>Записать и показать кадр: задний буфер залит цветом <paramref name="clearColor"/> (RGBA, 0…1).</summary>
    public void Render(Vector4 clearColor, bool vsync)
    {
        _device.BeginFrame();
        var list = _device.GraphicsList;
        list.ResourceBarrierTransition(_device.CurrentBackBuffer, ResourceStates.Present, ResourceStates.RenderTarget);
        list.ClearRenderTargetView(_device.CurrentRtv, new Color4(clearColor.X, clearColor.Y, clearColor.Z, clearColor.W));
        list.ResourceBarrierTransition(_device.CurrentBackBuffer, ResourceStates.RenderTarget, ResourceStates.Present);
        _device.EndFrame(vsync);
    }
}
