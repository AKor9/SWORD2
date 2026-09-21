using Vortice.Direct3D12;

namespace Sword.Engine.Rhi;

/// <summary>
/// Одна видимая шейдерам куча CBV/SRV/UAV, из которой раздаются индексы.
///
/// Смысл в том, что при Shader Model 6.6 шейдер адресует ресурс напрямую через
/// <c>ResourceDescriptorHeap[index]</c>. Корневая сигнатура перестаёт описывать
/// ресурсы: в ней остаются только константы. Двадцать проходов симуляции над
/// полутора десятками буферов не требуют ни одной таблицы дескрипторов и ни
/// одного пересоздания корневой сигнатуры — меняются только четыре корневые
/// константы между диспатчами.
/// </summary>
public sealed class BindlessHeap : IDisposable
{
    public const int DefaultCapacity = 4096;

    public ID3D12DescriptorHeap Heap { get; }
    public int Capacity { get; }

    private readonly int _increment;
    private readonly CpuDescriptorHandle _cpuStart;
    private readonly GpuDescriptorHandle _gpuStart;
    private readonly bool[] _allocated;

    public BindlessHeap(ID3D12Device device, int capacity)
    {
        Capacity = capacity;
        _allocated = new bool[capacity];
        var desc = new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            (uint)capacity,
            DescriptorHeapFlags.ShaderVisible);

        Heap = device.CreateDescriptorHeap(in desc);
        Heap.Name = "Sword.BindlessHeap";

        _increment = (int)device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        _cpuStart = Heap.GetCPUDescriptorHandleForHeapStart();
        _gpuStart = Heap.GetGPUDescriptorHandleForHeapStart();
    }

    /// <summary>
    /// Резервирует подряд идущие слоты. Непрерывность важна: набор буферов
    /// одной сущности (позиция, скорость, состояние) адресуется одним базовым
    /// индексом плюс константное смещение внутри шейдера.
    /// </summary>
    public int Allocate(int count = 1)
    {
        if (count <= 0 || count > Capacity)
            throw new ArgumentOutOfRangeException(nameof(count), count, $"Число дескрипторов должно быть от 1 до {Capacity}.");

        int run = 0;
        for (int i = 0; i < Capacity; i++)
        {
            run = _allocated[i] ? 0 : run + 1;
            if (run != count) continue;

            int index = i - count + 1;
            Array.Fill(_allocated, true, index, count);
            return index;
        }

        throw new InvalidOperationException($"Куча дескрипторов исчерпана: нет {count} свободных слотов из {Capacity}.");
    }

    /// <summary>Возвращает слоты после того, как GPU закончил использующий их кадр.</summary>
    internal void Free(int index, int count = 1)
    {
        if (index < 0 || count <= 0 || index > Capacity - count)
            throw new ArgumentOutOfRangeException(nameof(index), index, "Диапазон дескрипторов вне границ кучи.");

        for (int i = index; i < index + count; i++)
            if (!_allocated[i])
                throw new InvalidOperationException($"Дескриптор {i} уже свободен.");

        Array.Fill(_allocated, false, index, count);
    }

    public CpuDescriptorHandle Cpu(int index) => _cpuStart + index * _increment;

    public GpuDescriptorHandle Gpu(int index) => _gpuStart + index * _increment;

    public void Dispose() => Heap.Dispose();
}
