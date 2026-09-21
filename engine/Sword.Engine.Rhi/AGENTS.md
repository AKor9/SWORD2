# Sword.Engine.Rhi — правила

D3D12: устройство, очередь, свопчейн, кольцо кадров, bindless-куча, компиляция шейдеров (`ShaderLibrary`, DXC, своё
раскрытие `#include`, преамбула контракта строкой), буферы (`StaticBuffer`, `MappedStructuredRing`, `ConstantRing`).
Логика — из SWORD (`Sword.Engine/Graphics`). Сюда ходит только Render: ни игра, ни клиент движка типов Rhi не видят
(устройство создаёт `Renderer`; контракт шейдеров, конвейеры, корневая сигнатура — в Render). Проверяют
`CompiledAssemblies_FollowArchitecture` и `PublicSurfaceTests`.

- Vortice — только здесь и в Render, с `PrivateAssets=compile`: остальные сборки типов D3D12 не видят. Члены,
  нужные клиенту (размер, кадр, `Resize`, `WaitForIdle`), — без типов Vortice в сигнатурах.
- Очередь одна (graphics). Отдельная compute-очередь и межочередные заборы — только вместе с первой GPU-симуляцией
  (раздел 6 плана), не раньше.
- Версия Agility SDK — одна, `AgilitySdkVersion` в `Directory.Build.props`; сюда она приходит меткой сборки
  (`AssemblyMetadata`), `D3D12Core.dll` лежит в `D3D12\` рядом с exe. Число версии в код не вписывать.
- Ресурс, который может ещё читать GPU, освобождается через `DeferRelease`, а не `Dispose`.
- Сообщения отладочного слоя — в журнал, категория `d3d12`; `verify.ps1` (шаг `app-smoke`) считает любое такое
  сообщение ошибкой.

Проверка: `verify.ps1` (шаг `app-smoke`) или вручную
`Sword.exe --windowed 800x600 --frames 90 --resize-cycle --no-activate --debug --json`.
