# Sword.Engine.Render — правила

Рендер движка: граф, фазы кадра, HDR и тонмап, bloom, SMAA, точечный свет, прозрачное, текстуры, скин, UI, отладка, снимок. Устройство
и числа — [README.md](README.md), раздел 6 плана.

Инварианты:
- Публичный API — без типов Vortice, Rhi, Formats, Content (`PublicSurfaceTests`, `CompiledAssemblies_FollowArchitecture`).
- Барьеры, переходы, привязку целей, очистку и корневую сигнатуру ставит граф — руками не ставить. Цель чистится только своим значением
  очистки (`SceneColorClear` у `SceneColor`).
- С HDR до фазы Post в задний буфер не пишет никто (исключение графа); сцена — линейный цвет в `RenderFrame.SceneColor`; тонмап
  (затем `post.smaa.*` и `post.sharpen`, если включены) — последние проходы Post; UI и отладка — после них. Нейтральная постобработка
  (`Renderer.Post`) — кадр бит в бит прежний.
- SMAA: шейдер `common/smaa.hlsl` и CPU-эталон `SmaaMath` меняются вместе (`SmaaGpuTests`: GPU = эталон ±2/255).
- Тени: `SunShadow.Prepare` — до `SetFrameConstants` (матрицы читают заслоняющие), `AddPass` — после; каскад заслоняющих — слот 21
  (`SHADOW_CASCADE`), сэмплер сравнения — s4; тени точечных — `Lights.AddShadowPasses` до освещающего прохода, слоты 22–25. GTAO: `Ao.Prepare` до констант, глубина и нормали — до `AddPasses`, непрозрачный
  проход их не чистит. TAA: сцена — матрицей `Taa.Jitter`, вид — `Taa.SetView` до конца построения кадра (скорости — флагом, только если
  `Scene.Velocity` записана в этом кадре); оверлеи — без сдвига. Скин: палитра прошлого кадра — по `motionKey`, слот 17;
  `Skinned.Prepare` — после всех экземпляров и до проходов с ними, каждый такой проход — `Skinned.ReadBy` (иначе GPU-валидация).
- Кадр — 0 Б: списки и массивы — поля, растут удвоением; делегаты проходов создаются один раз; `LastFrame` каждый кадр не звать.
- Прозрачное по сцене — `Color(SceneColor).Depth(Scene.Depth, DepthAccess.Read).Read(Scene.Depth)`, премультиплицированная альфа;
  слой ½ — `Begin` и `Composite` раз за кадр.
- Числа, общие для C# и HLSL, — один раз в `Contribute`; структуры — `AddSize<T>` + `_Static_assert`; литералы шейдеров — ASCII.
- Формулы UE (тонмап, bloom, свет) — своими словами и числами, с пометкой отступлений; текст UE не копировать.
- Метод кадра с долгим циклом — тело цикла в отдельный метод (OSR); пиксельные циклы снимка — `AggressiveOptimization`.

Проверка: `dotnet test tests/Sword.Engine.Render.Tests` (GPU, слой отладки и GPU-валидация: любое предупреждение D3D12 — провал); игра —
`dotnet test tests/Sword.Game.Tests --filter "FullyQualifiedName~Arena&FullyQualifiedName~Gpu"`; `verify.ps1 -Only build,app-smoke`; `SWORD_TEST_KEEP=1`.

Типичные ошибки: забыть фазу прохода (без фазы — Opaque, до тонмапа); писать `BackBuffer` вместо `SceneColor` при HDR; очистка другим
цветом, чем у цели; `lerp` с цветами из констант вместо прежней формы — расхождение в 1 ULP half (день перестаёт быть бит в бит);
новый слот корневой константы без `ShaderContract.AddSlot`; кириллица в строке шейдера.
