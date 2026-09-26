# Sword.Game.Fire.Client — правила

Модуль `fire` (клиентский): огонь на горящем оружии игрока — сим Fluid2D на шишке, пламя, дым, свет. Устройство, числа, отступления от
L2 — [README.md](README.md); приёмка — [feature.json](feature.json).

Инварианты:
- Только представление: мир ECS не читается (оружие — сервис `IWeaponTips`), в симуляцию, хеш и реплей модуль ничего не пишет.
- Шаг сима — по `TickId` (60 Гц, заявки `FireStep`), время пламени и LF — по тикам и доле тика: реплей с тем же темпом кадров даёт те же
  поля (`fieldHash`) и тот же HDR; часов кадра нет.
- Формула меняется в двух местах сразу: шейдер `Shaders/fire/*.hlsl` и CPU-эталон (`FlameMath`, `SmokeMath`, `FireLightMath`); раскладка
  констант — `_Static_assert` с C#.
- Кадр — 0 Б: ресурсы — при старте на `maxFires` экземпляров, делегаты проходов — поля.
- Нет горящего оружия или шумов в паке — ни сима, ни проходов; модуль выключен — ни одного прохода и ресурса.
- Порядок в Transparent: дым (слой ½) раньше пламени.

Проверка: `dotnet test tests/Sword.Game.Tests --filter "FullyQualifiedName~Fire|FullyQualifiedName~Torch"` (GPU-тесты — пак игры);
`Sword.Headless --feature fire` (сценарии, виды App, бюджет); `verify.ps1 -Only without-modules`.

Агенту: `Sword.exe --json` → `fire` (`sims`, `metrics`, `flame`, `smoke`, `light`, `checks`), оверлеи `fire`, `fire.field`, `fire.flame`,
`fire.smoke`, `lights`, снимок `<снимок>.fire.*`, ручки `--tune fire.*`.

Типичные ошибки: правка шейдера без эталона; чтение поля GPU посреди кадра (только при выходе или асинхронно); сравнение цвета пламени
с кадрами L2 без ночи (`--tune light.night=1`); `--camera` ближе 6 м не подходит.
