# Пекарня игры (`Sword.Bake.Game`) — правила

Плагин пекарни: оружие — статический меш в кисти и зоны крови `.swblood` (`weapon.static`), анатомия на риге персонажа
(`anatomy.mesh`). Логика выпечки перенесена из SWORD 2eab13b как есть и выпекает эталонный пак байт в байт, кроме хвата
оружия (оружие — эталон, жёстко перенесённый в новый хват, `SkinBake.GripAttachment`) — менять её только по решению (кровь
и анатомия как фичи отложены, решения 15, 17; пекутся, чтобы пак был полон). Устройство — README.md.

- В `plugins` проекта — после `Sword.Bake.Engine`: ссылки на ядро и плагин движка — `Private=false`, `ExcludeAssets=runtime`
  (одна копия сборки, один тип `SkeletonParams` у `ctx.Ref`); обратный порядок — ошибка проекта «ссылается на плагин …».
- От плагина движка — только его открытый API (glTF, raw-экспорт, риг, хват, прореживание, `ContentFiles`, `MeshPreview`);
  своё — `internal`.
- Форматы пишут писатели: меш — `SkinnedMeshData` (Formats), зоны крови — `WeaponBloodData` (Game.Data), через
  `ContentFiles.Write`; раскладку формата здесь не повторять. Сторона `Neutral`; матрица — Bake, Bake.Engine, Content
  (источник предпросмотра `IAssetPreview`), Formats, Game.Data.

Проверка: `dotnet test tests/Sword.Bake.Game.Tests` (синтетика, оба плагина в `sword-bake`), с исходниками — категория
Source (сверка с эталоном — `artifacts/bake/source-check.json`, ключи `game*`).

Типичные ошибки: «для крепления grip нужны кости hand_r и фаланги …» — риг без пальцев правой кисти; «меш без скина —
анатомия сажается на кости рига (или задайте attach)» — статика без `attachBone`.
