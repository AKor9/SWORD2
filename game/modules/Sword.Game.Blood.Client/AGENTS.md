# Sword.Game.Blood.Client — правила

Модуль `blood` (клиентский, решение 20): кровь из ран по телу, брызги, кровь на оружии и на полу — только представление. Устройство,
сервисы, ручки, цена — [README.md](README.md).

Инварианты:
- Только представление: мира ECS модуль не видит — игра приходит сервисами `Sword.Game.Contracts` (`IBloodFeed`, `IFighterPoses`,
  `ISkinMesh`, `ITerrainView`, `ISceneSky`, `ISkinBloodLayer`, `IWeaponTips`, `IWeaponBloodLayer`); в симуляцию, хеш и реплей не пишет.
- Пары C# = HLSL меняются вместе: функция свежести `BloodLook.Evaluate` = `BloodLookEval` (`blood_look.hlsli`, `BloodSkinGpuTests`); полёт
  `BloodFlight` = `blood_flight.hlsli`; упаковка `SkinLayerTexel` и `WeaponBloodLayout` (Contracts) = `SkinLayerOf`/`WeaponLayerOf`
  (`scene_layers.hlsli` игры, `BloodLayerGpuTests`); приёмник `BloodFloor.Receives` = проверка `PsFloor`.
- Такт — `TickId` наблюдателя тиков: события и позы — ровно этого тика; часов кадра и `Random` нет (хеш: зерно, тик, касание); пауза —
  кровь стоит.
- Кадр — 0 Б: буферы при старте или удвоением, конвейеры и ленивые буферы прогреваются в первом кадре/шаге, делегаты — поля.
- Нет сервиса — нет того, что на нём стоит, игра стартует; модуль выключен или не собран (`-p:WithoutBlood=true`) — ни ресурса, ни прохода.
- Тик или кадр с долгим циклом — тело цикла в отдельный метод (OSR); поиск и скин кожи `SkinSurface` — `AggressiveOptimization`.
- Окно пола сдвигается только целыми текселями, уходящее — до шага в карту; засохшее — из `Advected`, не из `Composite`.

Проверка: `dotnet test tests/Sword.Game.Tests --filter "FullyQualifiedName~Blood"` (нужен пак), `verify.ps1 -Only without-modules`;
цена — бой `Sword.exe --windowed 1600x900 --no-activate --attack --units 3 --fps 60 --set combat.playerInvulnerable=true --frames 2400 --json`
(Release): `blood.feed.meanTickMs`/`p99TickMs`/`maxTickMs`, `report.passes`.

Видно: `--json` → `blood.feed`, `.services`, `.wounds`, `.skin`, `.drips`, `.floor` (`fieldHash`), `.spray`, `.blade`; оверлеи `blood`,
`blood.wounds`, `blood.drips`; F3 «Кровь» (`--tune blood.puddle=1`, `--tune blood.bladeSmear=1`); `blood.feed.deathsAt` — куда ставить `--camera`.

Типичные ошибки: читать `IBloodFeed` вне наблюдателя тиков (отдаётся последний сыгранный тик); якорь по позе кадра, а не тика; путать
`Point` пробы (центр капсулы) с касанием клинка (поверхность); новый путь удара без прогрева — всплеск JIT в тике первого удара.
