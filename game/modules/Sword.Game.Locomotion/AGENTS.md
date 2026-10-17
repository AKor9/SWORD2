# Sword.Game.Locomotion — правила

Модуль Locomotion (`locomotion`): локомоция клипами у врагов (и у игрока при `locomotion.player = clips` — значение в коде), Motion
Matching игрока при `mm` (`sword.json` игры). Описание — [README.md](README.md).

- Модуль не видит ядро игры (`Sword.Game`) и другие модули: вход — компоненты и метки `Game.Contracts`, клипы слотов —
  `Game.Data.ActionTable` (ресурс), позы — `PosePool` (ресурс) и `PoseRef` бойца.
- Позу не считает сам: только задания пулу поз, после `core.action-snapshot` (6в: снимок позы прошлого тика копируется
  первым) и до `core.animator-pose`; IK ног фиксации стоп игрока на MM (`locomotion.mm-feet`, `Motion/FootLock.cs`) — в Movement,
  после `core.root-collision` и до `core.pose-post-tick` (второй проход пула ядра), только модельные матрицы; нулевая правка — без
  задания. Порядок заданий и формулы — из `LegacyLocomotion` SWORD (идея); от них зависят хеши эталонов SWORD2 (участник `animation`) — разошлись эталоны, значит изменилось поведение. Сверка с SWORD снята на 7а.
- Свои позы (слой и снимок) модуль заводит сам и сам возвращает (`locomotion.release`); держит их инвариант модуля
  `locomotion.poses` (живые, свои, не итоговая поза бойца). Пул в инварианте — только через `IAnimationView`.
- `Added<Fighter>` у бойца, у которого состояние уже есть, — постройка заново (протокол `Game.Contracts`, сброс арены):
  состояние — с нуля, кроме стороны ходьбы `Dir` (`LegacyLocomotion.Reset` SWORD её не сбрасывает — трасса толпы 4д), позы
  слоя и снимка — те же (иначе утечка). Две записи одного бойца за срабатывание — одна команда.
- Без модуля игра стартует и ходит без клипов (решение 9): кода «на случай отсутствия» нет ни в ядре, ни здесь.
- Motion Matching (`Motion/`, этап 8): знание о контенте GASP (имена клипов, «стойка», «шаг и бег», суставы UE5 Manny, веса
  и пороги) — только в модуле, движок его не знает; умолчания — одно место, `GaspMotion` (ручки берут начальные значения
  оттуда, копий констант нет). Имя клипа — только классификация (стойка, походка); продолжение, направление и прошлое — по
  замеру (`MotionClipInfo`).
- База MM — одна на процесс и отбор (`GaspMotionBase.For`, кэш по экземпляру `ClipLibrary` из `PosePool`), неизменяема
  (тест `SharedMotionData_ExposeNoWritableData`). Ошибка данных — `InvalidDataException` с причиной; в игре — ошибка старта,
  без отката на клипы (решение 9).
- `locomotion.player = clips` регистрирует ровно то же, что до 8е: мир без `sword.json` (тесты, эталоны SWORD2) — побитово
  тот же (`ReferenceReplaysTests` без перезаписи). Компоненты, системы, ручки, участник хеша `locomotion` MM — только при `mm`.
- MM игрока (8е): корень и курс ведёт ядро; будущее запроса — модель контроллера тика из компонента `SteerModel` (одна правда — не
  копировать выражения контроллера в модуль); поза постройки игрока — `FighterStartPose` (кадр стойки базы, ставится при сборке мира);
  шаг — одно задание `PosePool.Motion` в `PoseRef` игрока (своих поз нет); игрок исключён из `LegacyLocomotion` (одну позу пишет одна
  система). Сброс — только по `Added<Fighter>` (флаг `Restart`), мёртвый (`Dead`) не трогается. Авторитетное состояние — компонент
  `MotionMatchState` (участник `ecs`, все числа конечны) и хранилище проигрывателей с историями (участник `locomotion`). Тик — 0 Б.
- Рука с мечом игрока на MM (8ж, `Motion/MotionMatchArm.cs`): после шага MM и до `core.animator-pose` (`locomotion.mm-arm`) — стойка
  оружия (клип слота `idle`, момент `SwordArm.StanceTime`): плечевой пояс — добавкой от нейтральной стойки MM поверх маха клипа, кисть —
  стойкой целиком, с весом `MotionMatchArm.Weight`
  (к ручке `locomotion.armWeight` не быстрее `SwordArm.WeightRate`/с); слой действий ядра — поверх руки. Имена суставов руки и её
  умолчания — одно место, `SwordArm`. Поза стойки — своя поза пула: заводит шаг руки, возвращает `locomotion.mm-arm-release`, держит
  инвариант `locomotion.mmArm`. Поза постройки игрока несёт руку слоем (`FighterStartPose.Layer*`; ядро считает его в `StartPoseModel`
  тем же сэмплом и смешиванием по маске `Skeleton.SubtreeMask`): задания руки и слой позы постройки меняются вместе, иначе первый тик
  после постройки прыгнет (`SwordArmTests.BuildReset_KnobOnTheRun_NoJumps`).
- На экран (8з; `Motion/MotionMatchObservers.cs` — только при `mm`): наблюдателям — только вид `IMotionMatchView` из
  `Game.Contracts` (клиент модуль не видит; вид только читает); события переходов `MotionTransition` шлёт
  `locomotion.mm-events` (Movement, до `locomotion.mm-history`); диагностика поиска (`MotionMatchDiagnostics`: последний поиск, время) — вне
  хеша и не влияет на поведение; тяжёлые инварианты `locomotion.mmDecay`, `locomotion.mmContinuity` (в списке `InvariantTests`); отпечаток базы — сведение реплея `locomotion.base`. Новое видимое MM — в вид и оверлей `mm` клиента.

Проверка: `tests/Sword.Game.Tests/AnimationTests`, `tests/Sword.Game.Tests/Locomotion/` (MM — `MotionMatchTests`, рука с мечом —
`SwordArmTests`, на экран — `MotionMatchViewTests`, `MotionMatchGpuTests`, на паке), эталоны SWORD2
(`ReferenceReplaysTests`); `verify.ps1` — `headless-smoke` (со `sword.json` — MM), `without-modules`.
