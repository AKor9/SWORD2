# Sword.Engine.Agent.Client — правила

Агент в живой игре: `ClientAgent` подключается к `ClientHost` как инструмент кадра (`IClientTool`) и ворота тиков
(`ITickGate`) — точка обслуживания раз в кадр после тиков (и на паузе, и при свёрнутом окне), пауза и шаги, снимок на
границе кадра. Инструменты: `capture.bundle`, `render.graph`. Транспорт — HTTP на 127.0.0.1.

- Клиент движка агента не знает: только общие контракты `Engine.Client` (`IClientTool`, `ITickGate`,
  `IClientObservable`, `ClientHost.RequestClose`).
- Пакет наблюдения собирается на потоке кадра после показа: камера и видимые сущности — от игры
  (`IClientObservable`), имена — `IEntityNames` игры, рамка на экране — проекция рамки мира (угол за камерой не
  считается).
- ID-буфер и оверлеи (5в) — только по заказу (`capture.bundle {"ids": true, "overlays": [...]}`): агент кладёт их в
  `FrameCapture` (`IdsPath`, `Layers`), игра видит заказ в `RenderFrame.Capture` и регистрирует свои проходы только в этом
  кадре (диагностический режим). Неизвестный оверлей — ошибка до снимка (`IClientObservable.Overlays`). Легенда ID-буфера —
  коды кадра из сводки рендера (`CaptureResult.Ids`) словами игры (`IClientObservable.DescribeId`) и именами `IEntityNames`;
  что заказано, но не снято, — `notes`.
- `render.graph` — граф, размер, кадр и тик, снятые в `Presented` (каждый показанный кадр, и из перетаскивания рамки):
  точка обслуживания идёт уже после тиков следующего кадра, и граф прошлого кадра с её тиком не сочетается.
- GPU-время (точка Пг): `render.graph` — `passes[].gpuMs` и `gpu` (`frameId` данных = показанный − `lagFrames`: сверка по
  номеру кадра рендера, отправленных к показу — `Renderer.FrameNumber` в `Presented`); `capture.bundle` — `gpu` кадра снимка
  (он ждёт простоя GPU — отставание 0, проходы с `gpuMs`). GPU-времена читаются на потоке кадра (обновляются на месте).
  Среднее за окно — `profile.stages` (`gpu`, в `Engine.Agent`), отдельного инструмента нет.

Проверка: `tests/Sword.Engine.Agent.Tests` (`CaptureGeometryTests`), `verify.ps1` шаг `agent-smoke` (живая игра; с 5в — ID-буфер:
сегменты игрока в его рамке, оверлей капсул), ID-проход — GPU-тест игры `IdBufferGpuTests`.
