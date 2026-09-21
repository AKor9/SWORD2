using Sword.Engine.Diagnostics;
using Sword.Engine.Rhi;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace Sword.Engine.Render;

/// <summary>Раскладка единственной корневой сигнатуры (<see cref="PipelineCache"/>).</summary>
internal static class RootLayout
{
    /// <summary>Корневых констант прохода (b2) — столько gPassN объявляет pass.hlsli.</summary>
    public const int ConstantCount = 28;
    public const int FrameCb = 0;
    public const int ViewCb = 1;
    public const int Constants = 2;
}

public enum BlendMode { Opaque, Additive, Alpha }

/// <summary>
/// Глубина конвейера. Обратный Z: сравнение GreaterEqual. <see cref="TestBehind"/> — проходит то, что ЗА записанной
/// глубиной (задние грани объёма декали за поверхностью), без записи.
/// </summary>
public enum DepthMode { None, Test, TestWrite, TestBehind }

/// <summary>
/// Отсечение граней. Лицевые у конвейеров — по часовой: <see cref="Back"/> отсекает намотанные против часовой
/// (как <c>cullBack</c> SWORD), <see cref="Front"/> — по часовой.
/// </summary>
public enum CullMode { None, Back, Front }

/// <summary>
/// Графический конвейер без типов D3D12: шейдеры и состояние. Форматы целей сюда не входят — их даёт проход,
/// в котором конвейер используется (<see cref="TargetLayout"/>).
/// </summary>
public readonly record struct GraphicsPipelineDesc(
    string File,
    string VsEntry,
    string PsEntry,
    BlendMode Blend = BlendMode.Opaque,
    DepthMode Depth = DepthMode.None,
    CullMode Cull = CullMode.None,
    bool Lines = false,
    string? Defines = null);

/// <summary>Вычислительный конвейер: файл, точка входа, определения. Компилируется строго по IEEE (как симуляция).</summary>
public readonly record struct ComputePipelineDesc(string File, string Entry, string? Defines = null);

/// <summary>Форматы целей прохода: до двух целей цвета и глубина (<see cref="Format.Unknown"/> — нет).</summary>
internal readonly record struct TargetLayout(Format Color0, Format Color1 = Format.Unknown, Format Depth = Format.Unknown)
{
    public int ColorCount => Color0 == Format.Unknown ? 0 : Color1 == Format.Unknown ? 1 : 2;
}

/// <summary>
/// Единственная корневая сигнатура (bindless: b0, b1 CBV + <see cref="RootLayout.ConstantCount"/> корневых констант
/// в b2 + статические сэмплеры s0–s2) и кеш конвейеров поверх неё. Ресурсы шейдер берёт из <c>ResourceDescriptorHeap</c>
/// по индексу, пришедшему в константах. Перенос <c>Graphics/PipelineCache.cs</c> SWORD 2eab13b; форматы целей теперь
/// — часть ключа (их объявляет проход рендер-графа), а не набор «задний буфер или цели сцены».
/// </summary>
internal sealed class PipelineCache : IDisposable
{
    public ID3D12RootSignature RootSignature { get; }

    private readonly GraphicsDevice _gd;
    private readonly ShaderLibrary _shaders;

    private readonly record struct Key(GraphicsPipelineDesc? Graphics, TargetLayout Layout, string? ComputeFile, string? ComputeEntry, string? ComputeDefines);

    private readonly Dictionary<Key, ID3D12PipelineState> _pipelines = [];
    private readonly Dictionary<Key, ShaderKey[]> _pipelineShaders = [];
    private readonly HashSet<ShaderKey> _retry = [];

    public int PipelineCount => _pipelines.Count;

    public PipelineCache(GraphicsDevice gd, ShaderLibrary shaders)
    {
        _gd = gd;
        _shaders = shaders;

        var parameters = new RootParameter1[3];
        parameters[RootLayout.FrameCb] = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        parameters[RootLayout.ViewCb] = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All);
        parameters[RootLayout.Constants] = new RootParameter1(new RootConstants(2, 0, RootLayout.ConstantCount), ShaderVisibility.All);

        var samplers = new[]
        {
            new StaticSamplerDescription(ShaderVisibility.All, 0, 0)
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunction = ComparisonFunction.Always,
                MaxLOD = float.MaxValue,
            },
            new StaticSamplerDescription(ShaderVisibility.All, 1, 0)
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap,
                ComparisonFunction = ComparisonFunction.Always,
                MaxLOD = float.MaxValue,
            },
            // Декали и поверхности под острым углом: мипы по большей производной мылят, анизотропия — нет.
            new StaticSamplerDescription(ShaderVisibility.All, 2, 0)
            {
                Filter = Filter.Anisotropic,
                MaxAnisotropy = 8,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunction = ComparisonFunction.Always,
                MaxLOD = float.MaxValue,
            },
        };

        var desc = new RootSignatureDescription1(
            RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
            parameters, samplers);
        RootSignature = gd.Device.CreateRootSignature(in desc);
        RootSignature.Name = "Sword.RootSignature";
    }

    public ID3D12PipelineState GetCompute(string file, string entry, string? defines = null)
    {
        var key = new Key(null, default, file, entry, defines);
        if (_pipelines.TryGetValue(key, out var pso)) return pso;

        var shaderKey = new ShaderKey(file, entry, DxcShaderStage.Compute, defines);
        pso = BuildCompute(_shaders.Get(shaderKey, simulation: true).Bytecode, $"{file}|{entry}");
        _pipelines[key] = pso;
        _pipelineShaders[key] = [shaderKey];
        return pso;
    }

    public ID3D12PipelineState GetGraphics(in GraphicsPipelineDesc desc, in TargetLayout layout)
    {
        var key = new Key(desc, layout, null, null, null);
        if (_pipelines.TryGetValue(key, out var pso)) return pso;

        var vsKey = new ShaderKey(desc.File, desc.VsEntry, DxcShaderStage.Vertex, desc.Defines);
        var psKey = new ShaderKey(desc.File, desc.PsEntry, DxcShaderStage.Pixel, desc.Defines);
        pso = BuildGraphics(desc, layout, _shaders.Get(vsKey, false).Bytecode, _shaders.Get(psKey, false).Bytecode);
        _pipelines[key] = pso;
        _pipelineShaders[key] = [vsKey, psKey];
        return pso;
    }

    private ID3D12PipelineState BuildCompute(ReadOnlyMemory<byte> cs, string name)
    {
        var pso = _gd.Device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = RootSignature,
            ComputeShader = cs,
        });
        pso.Name = name;
        return pso;
    }

    private ID3D12PipelineState BuildGraphics(in GraphicsPipelineDesc d, in TargetLayout layout, ReadOnlyMemory<byte> vs, ReadOnlyMemory<byte> ps)
    {
        var blendDesc = BlendDescription.Opaque;
        if (d.Blend == BlendMode.Additive)
            blendDesc.RenderTarget[0] = new RenderTargetBlendDescription(true, false,
                Blend.SourceAlpha, Blend.One, BlendOperation.Add, Blend.One, Blend.One, BlendOperation.Add);
        else if (d.Blend == BlendMode.Alpha)
            blendDesc.RenderTarget[0] = new RenderTargetBlendDescription(true, false,
                Blend.SourceAlpha, Blend.InverseSourceAlpha, BlendOperation.Add, Blend.One, Blend.InverseSourceAlpha, BlendOperation.Add);

        if (d.Depth != DepthMode.None && layout.Depth == Format.Unknown)
            throw new InvalidOperationException($"{d.File}:{d.VsEntry}/{d.PsEntry}: глубина {d.Depth} в проходе без цели глубины");

        var depthDesc = d.Depth switch
        {
            DepthMode.TestWrite => new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.GreaterEqual),
            DepthMode.Test => new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.GreaterEqual),
            DepthMode.TestBehind => new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.LessEqual),
            _ => DepthStencilDescription.None,
        };

        Format[] colors = layout.ColorCount switch
        {
            0 => [],
            1 => [layout.Color0],
            _ => [layout.Color0, layout.Color1],
        };

        var pso = _gd.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = RootSignature,
            VertexShader = vs,
            PixelShader = ps,
            BlendState = blendDesc,
            SampleMask = uint.MaxValue,
            RasterizerState = d.Cull switch
            {
                CullMode.Front => RasterizerDescription.CullClockwise,
                CullMode.Back => RasterizerDescription.CullCounterClockwise,
                _ => RasterizerDescription.CullNone,
            },
            DepthStencilState = depthDesc,
            PrimitiveTopologyType = d.Lines ? PrimitiveTopologyType.Line : PrimitiveTopologyType.Triangle,
            RenderTargetFormats = colors,
            // Формат глубины — прохода, даже если конвейер её не трогает: иначе он не совпал бы с привязанной целью.
            DepthStencilFormat = layout.Depth,
            SampleDescription = new SampleDescription(1, 0),
        });
        pso.Name = $"{d.File}|{d.VsEntry}|{d.PsEntry}";
        return pso;
    }

    /// <summary>Горячая перезагрузка: изменённые шейдеры перекомпилируются, старый конвейер остаётся при ошибке.</summary>
    public int ReloadStale()
    {
        var stale = _shaders.FindStale();
        foreach (var key in _retry)
            if (!stale.Contains(key)) stale.Add(key);
        if (stale.Count == 0) return 0;

        foreach (var key in stale) _shaders.Invalidate(key);
        _retry.Clear();

        int reloaded = 0;
        foreach (var (key, keys) in _pipelineShaders.ToList())
        {
            if (!keys.Any(stale.Contains)) continue;
            try
            {
                var old = _pipelines[key];
                ID3D12PipelineState fresh = key.Graphics is { } g
                    ? BuildGraphics(g, key.Layout, _shaders.Get(keys[0], false).Bytecode, _shaders.Get(keys[1], false).Bytecode)
                    : BuildCompute(_shaders.Get(keys[0], true).Bytecode, $"{key.ComputeFile}|{key.ComputeEntry}");
                _pipelines[key] = fresh;
                _gd.DeferRelease(old);
                reloaded++;
            }
            catch (Exception ex)
            {
                foreach (var k in keys) _retry.Add(k);
                Log.Warning("shader", $"перезагрузка {keys[0].File}:{keys[0].Entry} не удалась:\n{ex.Message}");
            }
        }
        return reloaded;
    }

    public void Dispose()
    {
        foreach (var pso in _pipelines.Values) pso.Dispose();
        RootSignature.Dispose();
    }
}
