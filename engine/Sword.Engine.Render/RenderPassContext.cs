using Vortice.Direct3D12;

namespace Sword.Engine.Render;

/// <summary>
/// Запись работы прохода без типов D3D12: конвейер, корневые константы, отрисовка (графический проход) или dispatch
/// (вычислительный). Цели, область вывода, корневую сигнатуру и константы кадра уже поставил граф. Корневые константы
/// в начале каждого прохода — нули: проходы не видят чужих слотов.
/// </summary>
public sealed class RenderPassContext
{
    private readonly PipelineCache _pipelines;
    private readonly uint[] _constants = new uint[RootLayout.ConstantCount];
    private ID3D12GraphicsCommandList? _list;
    private TargetLayout _layout;
    private bool _constantsDirty;
    private bool _pipelineSet;

    internal RenderPassContext(PipelineCache pipelines) => _pipelines = pipelines;

    /// <summary>Вид текущего прохода.</summary>
    public PassKind Kind { get; private set; }

    /// <summary>Размер целей графического прохода, пиксели; у вычислительного — 0.</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Вызовов отрисовки в проходе.</summary>
    public int DrawCalls { get; private set; }

    /// <summary>Вызовов dispatch в проходе.</summary>
    public int Dispatches { get; private set; }

    internal void Begin(ID3D12GraphicsCommandList list, PassKind kind, TargetLayout layout, int width, int height)
    {
        _list = list;
        Kind = kind;
        _layout = layout;
        Width = width;
        Height = height;
        DrawCalls = Dispatches = 0;
        Array.Clear(_constants);
        _constantsDirty = true;
        _pipelineSet = false;
    }

    /// <summary>Графический конвейер для следующих отрисовок; форматы целей — этого прохода.</summary>
    public void SetPipeline(in GraphicsPipelineDesc desc)
    {
        Require(PassKind.Graphics, "графический конвейер");
        _list!.SetPipelineState(_pipelines.GetGraphics(desc, _layout));
        _pipelineSet = true;
    }

    /// <summary>Вычислительный конвейер для следующих dispatch.</summary>
    public void SetPipeline(in ComputePipelineDesc desc)
    {
        Require(PassKind.Compute, "вычислительный конвейер");
        _list!.SetPipelineState(_pipelines.GetCompute(desc.File, desc.Entry, desc.Defines));
        _pipelineSet = true;
    }

    /// <summary>Корневая константа прохода (gPassN шейдера, N = <paramref name="slot"/>).</summary>
    public void SetConstant(int slot, uint value)
    {
        if ((uint)slot >= RootLayout.ConstantCount)
            throw new ArgumentOutOfRangeException(nameof(slot), slot, $"корневых констант {RootLayout.ConstantCount}");
        _constants[slot] = value;
        _constantsDirty = true;
    }

    public void SetConstant(int slot, int value) => SetConstant(slot, unchecked((uint)value));

    public void SetConstant(int slot, float value) => SetConstant(slot, BitConverter.SingleToUInt32Bits(value));

    /// <summary>
    /// Обрезка следующих отрисовок: прямоугольник в пикселях цели [<paramref name="left"/>, <paramref name="right"/>) ×
    /// [<paramref name="top"/>, <paramref name="bottom"/>), зажимается в цель. В начале прохода — вся цель.
    /// </summary>
    public void SetScissor(int left, int top, int right, int bottom)
    {
        Require(PassKind.Graphics, "обрезка");
        left = Math.Clamp(left, 0, Width);
        right = Math.Clamp(right, left, Width);
        top = Math.Clamp(top, 0, Height);
        bottom = Math.Clamp(bottom, top, Height);
        _list!.RSSetScissorRect(new Vortice.RawRect(left, top, right, bottom));
    }

    /// <summary>Вершины из SV_VertexID, без вершинного и индексного буферов.</summary>
    public void Draw(uint vertexCount, uint instanceCount = 1)
    {
        Flush(PassKind.Graphics, "отрисовка");
        _list!.DrawInstanced(vertexCount, instanceCount, 0, 0);
        DrawCalls++;
    }

    /// <summary>Треугольники по индексам; <paramref name="indexCount"/> 0 — все с <paramref name="startIndex"/>.</summary>
    public void DrawIndexed(GpuIndices indices, uint instanceCount = 1, uint startIndex = 0, uint indexCount = 0)
    {
        if (startIndex > indices.Count) throw new ArgumentOutOfRangeException(nameof(startIndex), startIndex, $"индексов {indices.Count}");
        Flush(PassKind.Graphics, "отрисовка");
        _list!.IASetIndexBuffer(indices.View);
        _list.DrawIndexedInstanced(indexCount == 0 ? indices.Count - startIndex : indexCount, instanceCount, startIndex, 0, 0);
        DrawCalls++;
    }

    /// <summary>Групп потоков по осям (размер группы — <c>[numthreads]</c> шейдера).</summary>
    public void Dispatch(uint groupsX, uint groupsY = 1, uint groupsZ = 1)
    {
        Flush(PassKind.Compute, "dispatch");
        _list!.Dispatch(groupsX, groupsY, groupsZ);
        Dispatches++;
    }

    private void Require(PassKind kind, string what)
    {
        if (Kind != kind)
            throw new InvalidOperationException($"{what} в {(Kind == PassKind.Graphics ? "графическом" : "вычислительном")} проходе");
    }

    private void Flush(PassKind kind, string what)
    {
        Require(kind, what);
        if (!_pipelineSet) throw new InvalidOperationException($"{what} без конвейера: сначала SetPipeline");
        if (!_constantsDirty) return;
        if (Kind == PassKind.Graphics) _list!.SetGraphicsRoot32BitConstants<uint>(RootLayout.Constants, _constants, 0);
        else _list!.SetComputeRoot32BitConstants<uint>(RootLayout.Constants, _constants, 0);
        _constantsDirty = false;
    }
}
