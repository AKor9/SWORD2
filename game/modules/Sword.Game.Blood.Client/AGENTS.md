# Sword.Game.Blood.Client — правила

Модуль `blood` (клиентский, решение 20): кровь из ран по телу и на полу — только представление. Устройство, сервисы, числа —
[README.md](README.md); план этапа — `artifacts/plans/blood/PLAN.md`.

Инварианты:
- Только представление: мира ECS модуль не видит (ни `Entity`, ни запросов) — игра приходит сервисами `Sword.Game.Contracts`
  (`IBloodFeed`, `IFighterPoses`, `ISkinMesh`, `ITerrainView`, `ISceneSky`, `ISkinBloodLayer`); в симуляцию, хеш и реплей модуль ничего не пишет.
- Функция свежести — одна: `BloodLook.Evaluate` (C#) и `BloodLookEval` (`Shaders/blood/blood_look.hlsli`) меняются вместе
  (`BloodSkinGpuTests`); упаковка вида вершины — `SkinLayerTexel` (Contracts) = `SkinLayerOf` в `scene.hlsl` игры.
- Такт — по `TickId` наблюдателя тиков (`AfterTick`): события и позы читаются там, где они ровно этого тика; часов кадра нет, пауза — кровь
  стоит. Рождение случайного — из хеша (зерно, `TickId`, `ContactIndex`), не из `Random`.
- Кадр модуля и сервисов — 0 Б: буферы заводятся при старте или растут удвоением, делегаты проходов — поля.
- Нет сервиса — модуль не делает того, что на нём стоит, и игра стартует; модуль выключен (`modules.blood.enabled=false`) или не собран
  (`-p:WithoutBlood=true`) — ни ресурса, ни прохода, ни ручки.
- Метод тика или кадра с долгим циклом — тело цикла в отдельный метод (OSR).
- Полёт капли — одна формула: `BloodFlight` (C#) = `blood_flight.hlsli`; приёмник плёнки `BloodFloor.Receives` = проверка `PsFloor`; окно пола
  сдвигается только целыми текселями, а уходящее из окна до шага уходит в карту (`blood.floor.transfer`); засохшее — из состояния между
  шагами (`Advected`), не из `Composite` (его следующий шаг не читает).

Проверка: `dotnet test tests/Sword.Game.Tests --filter "FullyQualifiedName~Blood"` (GPU и Content — пак игры);
`verify.ps1 -Only without-modules`.

Видно: `Sword.exe --json` → `blood.feed` (тики, попадания, смерти, постройки, сбросы, последний кадр, бойцов в позах),
`blood.services`, `blood.wounds` (с `ooze`), `blood.skin`, `blood.drips`, `blood.floor` (`fieldHash`, мл окна и карты); оверлеи `blood` (с рамкой
окна пола и приземлениями), `blood.wounds`, `blood.drips`; вкладка F3 «Кровь» (лужа под игроком: `--tune blood.puddle=1`). Снимки боя —
`--attack` (удар каждый тик) и `--fps 60`: прогон повторяется, `blood.feed.deathsAt` — куда ставить `--camera`.

Типичные ошибки: читать `IBloodFeed` вне наблюдателя тиков и ждать всех тиков кадра (отдаётся последний сыгранный); якорь раны по позе
кадра, а не тика; путать `Point` пробы удара (центр капсулы) с касанием клинка (поверхность).
