using System.Reflection;
using SharpGen.Runtime;
using Vortice.Direct3D12;

namespace Sword.Engine.Rhi;

/// <summary>
/// Выбирает app-local D3D12 runtime до создания первого устройства — через независимую фабрику устройств: экспорты
/// D3D12SDKVersion/D3D12SDKPath managed apphost не годятся.
/// </summary>
internal static class AgilitySdk
{
    /// <summary>Имя метки сборки, в которой лежит версия пакета. Ставит Sword.Engine.Rhi.csproj.</summary>
    private const string VersionMetadataKey = "AgilitySdkVersion";

    private static readonly object Gate = new();
    private static ID3D12DeviceFactory? _factory;

    public static void Initialize()
    {
        if (_factory is not null) return;

        lock (Gate)
        {
            if (_factory is not null) return;

            string sdkDirectory = Path.Combine(AppContext.BaseDirectory, "D3D12");
            string corePath = Path.Combine(sdkDirectory, "D3D12Core.dll");
            if (!File.Exists(corePath))
                throw new FileNotFoundException(
                    "Не найден app-local DirectX 12 Agility runtime. " +
                    "Копируйте приложение вместе с каталогом D3D12.",
                    corePath);

            D3D12.D3D12GetInterface(
                D3D12.D3D12SDKConfigurationClsId,
                out ID3D12SDKConfiguration1? configuration).CheckError();

            using (configuration)
            {
                string sdkPath = Path.TrimEndingDirectorySeparator(sdkDirectory)
                    + Path.DirectorySeparatorChar;
                var factory = configuration!.CreateDeviceFactory(
                    ResolveVersion(),
                    sdkPath);
                try
                {
                    // Перенаправляет обычные D3D12CreateDevice, сериализацию
                    // root signature и debug interface в выбранный runtime.
                    factory.ApplyToGlobalState().CheckError();
                    _factory = factory;
                }
                catch
                {
                    factory.Dispose();
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Номер версии для <c>CreateDeviceFactory</c> — средняя часть версии пакета: «1.619.5» → 619. Читается из метки сборки:
    /// версия объявлена один раз (<c>AgilitySdkVersion</c> в Directory.Build.props). Нет метки — отказ, без числа по умолчанию.
    /// </summary>
    private static uint ResolveVersion()
    {
        string? text = typeof(AgilitySdk).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == VersionMetadataKey)?.Value;

        if (string.IsNullOrEmpty(text))
            throw new InvalidOperationException(
                $"В сборке нет метки {VersionMetadataKey}. Свойство AgilitySdkVersion " +
                "из Directory.Build.props не доехало до Sword.Engine.Rhi.csproj.");

        string[] parts = text.Split('.');
        if (parts.Length != 3 || !uint.TryParse(parts[1], out uint version))
            throw new InvalidOperationException(
                $"{VersionMetadataKey} = «{text}»: ожидался вид 1.619.5, " +
                "средняя часть — номер версии Agility SDK.");

        return version;
    }
}
