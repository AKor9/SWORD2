# tools/ue — скрипты Unreal

Скрипты Python для Unreal Editor 5.8: ретаргет клипов на UE5 Manny и выгрузка клипов, мешей, оружия, анатомии и
текстур в исходники пекарни `${src}/_Export/*`. Выполняются в Unreal, вне сборки SWORD2. Источник — SWORD `tools/ue`
(коммит `2eab13b`); в SWORD2 — только конвейер, без разовых исследований.

Для перепекания с другими параметрами выпечки скрипты не нужны: выгрузка лежит в `_Export`. Они нужны, если менять
то, что решается в Unreal: ретаргет, частоту, отбор клипов, флаг цикла, кости и кривые, LOD анатомии, набор текстур.

## Пути машины

| что | путь | где задан |
|---|---|---|
| Unreal | `D:/Progs/Epic Games/UE_5.8/Engine/Binaries/Win64/UnrealEditor-Cmd.exe` | команды ниже |
| рабочая копия GASP (UE-проект) | `D:/Temp/GamedevAssets/GameAnimationSample_SWORD/GameAnimationSample.uproject` | команды ниже |
| корень выгрузок (`${src}/_Export` пекарни, `${src}` = `D:/Temp/GamedevAssets`) | `D:/Temp/GamedevAssets/_Export` | `EXPORT_ROOT` вверху `export_meshes.py`, `export_weapons.py`, `export_organs.py`, `export_textures.py`; поле `out` каждого `export_config*.json` |
| папка скриптов | `D:/Work/Visual Studio/SWORD2/tools/ue` | `HERE` в `retarget_gasp.py` — папка скрипта, а если в commandlet не задан `__file__` — этот путь |

Скрипты работают в рабочей копии GASP, не в скачанном проекте: в неё скопированы паки `/Game/1HM_Sword_Attacks_Finishers`,
`/Game/CombatMasterAnimBundle`, `/Game/HumanOrgans`, `/Game/RealisticBlood`, туда же пишутся результаты ретаргета
(`/Game/SWORD/…`). Выгрузки и контент паков — вне git (лицензии).

## Скрипты

| скрипт | что делает |
|---|---|
| `retarget_gasp.py` | Локомоция GASP (UEFN Mannequin) → UE5 Manny. Ретаргетер — копия `RTG_UEFN_to_UE5_Mannequin` в `/Game/SWORD/Rigs/RTG_UEFN_to_UE5_Mannequin_SWORD`: у ног Blend to Source = 0 (иначе опора скользит на 3 %, циклы не замыкаются), Pelvis Motion `scale_horizontal` = 1. Клипы — по `root`, `include`, `exclude` из `export_config.json`; копии — в `/Game/SWORD/GASP_UE5/<папка GASP>`, папка перед прогоном удаляется. |
| `retarget_pack.py` | Пак на скелете UE4 Mannequin → UE5 Manny. Ретаргетер `/Game/SWORD/Retarget/RTG_UE4_to_UE5`: стек операций по умолчанию, автосопоставление цепочек, поза цели выровнена по источнику, пястные кости — в позе меша. Копии — в `SWORD_RTG_OUT/<папки пака>`, папка перед прогоном удаляется. Один скрипт на все паки: пак, набор и меш — переменными окружения. |
| `export_gasp.py` | Клипы → raw-формат: `manifest.json` (`sword-rawanim` v1: кости, родители, `units cm`, `axes ue`, `rootForward`, клипы — имя, путь UE, fps, кадры, цикл, кривые, sync markers, notifies) и `clips/<имя>.bin` (кадры × кости × 7 float32: позиция в см, кватернион xyzw; затем кадры × кривые). Кадр `f` — время `f / sampleFps`; позы — в component space, root motion остаётся в кости `root`. Конфиг — `SWORD_EXPORT_CONFIG`, без неё — `export_config.json` рядом со скриптом. |
| `export_meshes.py` | `manny.glb` (SKM_Manny со скин-весами), `sword_1h.glb` (меч пака 1HM), `skeleton.json` (поза покоя UE5 Manny: локальная и мировая, сокеты скелета и меша; сокеты скелета UE4 пака) → `_Export/meshes`. В лог — блюпринты пака 1HM (к какой кости крепится меч). |
| `export_weapons.py` | Все StaticMesh из `SWORD_WEAPON_ROOTS` → `_Export/weapons/<имя ассета>.glb`. |
| `export_organs.py` | Меши HumanOrgans → `_Export/organs/<имя>.glb`, `stats.json`, `export.log`. У скелетного меша берётся первый LOD, который влезает в бюджет вершин `SWORD_ORGANS_BUDGET`; если такого нет — досоздаёт LOD (до 5) и **сохраняет ассет в UE-проекте**. Статика — LOD0 (в commandlet её не упростить), прореживает пекарня (`maxTriangles`). |
| `export_textures.py` | Все Texture2D из `SWORD_TEX_ROOTS` → PNG (не вышло — TGA) с путями относительно корня, `textures.json` (размер, sRGB, сжатие UE). |

## Конфиги клипов → выгрузка → манифест пекарни

Манифесты — проект пекарни [`Bake/`](../../Bake/README.md). У конфигов, чьи выгрузки
читает пекарня, `sampleFps` 30, `bones: ["*"]` (все кости, иерархия — из `parentsMesh`), кривых нет.

| конфиг `export_gasp.py` | откуда (UE) | выгрузка | манифест, поле |
|---|---|---|---|
| `export_config_gasp_skin.json` | `/Game/SWORD/GASP_UE5` — после `retarget_gasp.py` | `skin_gasp` | `anim/gasp` — `anims` (набор `locomotion`) |
| `export_config_skin.json` | `/Game/SWORD/AnimationsUE5` — после `retarget_pack.py` по умолчанию (1HM) | `skin_sword` | `anim/1hm_sword` — `anims`; `skeletons/ue5_manny` — `clips` (эталон костей рига) |
| `export_config_ue4src.json` | `/Game/1HM_Sword_Attacks_Finishers/Animations` — исходный скелет UE4 | `skin_sword_ue4` | `anim/1hm_sword`, `skeletons/ue5_manny` — `fingerSrc` |
| `export_config_combat.json` | `/Game/SWORD/AnimationsCombat` — после трёх прогонов `retarget_pack.py` | `skin_combat` | `anim/combat_master` — `anims` |
| `export_config_combat_ue4src.json` | `/Game/CombatMasterAnimBundle/Animations`, 3 набора — исходный UE4 | `skin_combat_ue4` | `anim/combat_master` — `fingerSrc` |
| `export_config_combat_ext.json` | `/Game/SWORD/AnimationsCombatExt` — после девяти прогонов `retarget_pack.py` | `skin_combat_ext` | `anim/combat_master_ext` — `anims` |
| `export_config_combat_ext_ue4src.json` | `/Game/CombatMasterAnimBundle/Animations`, 9 наборов — исходный UE4 | `skin_combat_ext_ue4` | `anim/combat_master_ext` — `fingerSrc` |
| `export_config.json` | `/Game/Characters/UEFN_Mannequin/Animations` | `gasp` (23 кости UEFN) | — |

`export_config.json` живёт как список регулярок отбора для `retarget_gasp.py`; его выгрузку `_Export/gasp` (конфиг
`export_gasp.py` по умолчанию) пекарня не читает.

Остальные выгрузки:

| скрипт | выгрузка | манифест, поле |
|---|---|---|
| `export_meshes.py` | `meshes/manny.glb`, `meshes/skeleton.json`, `meshes/sword_1h.glb` | `characters/manny/mesh` — `mesh`; `skeletons/ue5_manny` — `refPose`; `weapons/sword_1h` — `mesh` |
| `export_weapons.py` | `weapons/SM_*.glb` | `weapons/{combat_sword,katana,katana_v2,powerful_sword,sword_dss}` — `mesh` |
| `export_organs.py` | `organs/SK_Skeleton.glb`, `organs/SK_SkeletonWithOrgans.glb`, `organs/BrainHighPoly.glb` | `characters/manny/anatomy/{skeleton,organs,brain}` — `mesh` |
| `export_textures.py` по умолчанию | `blood/**` | `effects/blood/{directional,noise,omni,puddles,spots}` |
| `export_textures.py` для анатомии | `organs/textures/*_D.png`, `*_NM.png` | `characters/manny/anatomy/{albedo,normals}` — `layers` |

Не из Unreal: шрифты и `combat/actions` (`${src}/_Sword`), `effects/innards/cut_albedo` (FBX и PNG пака gore props).

## Как запускать

Из PowerShell. Путь к скрипту с пробелами UE ждёт в виде `-script="D:/…/x.py"` (так в логах запусков UE-проекта);
PowerShell даёт этот вид (`Invoke-UeScript` ниже), Git Bash — нет: он берёт в кавычки весь аргумент (`"-script=D:/…"`).
Переменные окружения в PowerShell не портят пути `/Game/…` (в Git Bash нужен `MSYS_NO_PATHCONV=1`).

```powershell
$ue   = 'D:/Progs/Epic Games/UE_5.8/Engine/Binaries/Win64/UnrealEditor-Cmd.exe'
$proj = 'D:/Temp/GamedevAssets/GameAnimationSample_SWORD/GameAnimationSample.uproject'
$dir  = 'D:/Work/Visual Studio/SWORD2/tools/ue'
# клипы — с -nullrhi, текстуры — без него (так в логах прогонов SWORD); glTF — без него: экспортёр запекает материалы
$anim = '-EnablePlugins=PythonScriptPlugin,EditorScriptingUtilities', '-nullrhi', '-unattended', '-nosplash', '-nopause', '-stdout'
$tex  = '-EnablePlugins=PythonScriptPlugin,EditorScriptingUtilities', '-unattended', '-nosplash', '-nopause', '-stdout'
$gltf = '-EnablePlugins=PythonScriptPlugin,EditorScriptingUtilities,GLTFExporter', '-unattended', '-nosplash', '-nopause', '-stdout'
function Invoke-UeScript([string]$Script, [string[]]$Flags, [hashtable]$Env = @{}) {
    foreach ($k in $Env.Keys) { Set-Item "Env:$k" $Env[$k] }
    try { & $ue $proj -run=pythonscript "-script=`"$dir/$Script`"" @Flags }
    finally { foreach ($k in $Env.Keys) { Remove-Item "Env:$k" } }
}
```

Локомоция GASP (`anim/gasp`):

```powershell
Invoke-UeScript retarget_gasp.py $anim
Invoke-UeScript export_gasp.py $anim @{ SWORD_EXPORT_CONFIG = "$dir/export_config_gasp_skin.json" }
```

Пак 1HM (`anim/1hm_sword`, `skeletons/ue5_manny`):

```powershell
Invoke-UeScript retarget_pack.py $anim
Invoke-UeScript export_gasp.py $anim @{ SWORD_EXPORT_CONFIG = "$dir/export_config_skin.json" }
Invoke-UeScript export_gasp.py $anim @{ SWORD_EXPORT_CONFIG = "$dir/export_config_ue4src.json" }
```

Combat Master — прогон `retarget_pack.py` на набор: у каждого набора свой ассет скелета (кости те же), поэтому свой
меш источника. Меши — `/Game/CombatMasterAnimBundle/Demo/Characters/Mannequin_UE4/Meshes/SK_Mannequin<суффикс>`.

| манифест | набор (`SWORD_RTG_INCLUDE` = `/<набор>/`) | суффикс меша | `SWORD_RTG_OUT` |
|---|---|---|---|
| `anim/combat_master` | `PowerfulSwordAnims` | `_PowerfulSword` | `/Game/SWORD/AnimationsCombat/PowerfulSword` |
| | `HandsomeSwordCombatAnims` | `_Sword_1H` | `/Game/SWORD/AnimationsCombat/HandsomeSword` |
| | `HandsomeSwordAnimsV2` | `_Sword_1H_V2` | `/Game/SWORD/AnimationsCombat/HandsomeSwordV2` |
| `anim/combat_master_ext` | `DynamicHandtoHandAnims` | `_DHTH` | `/Game/SWORD/AnimationsCombatExt/<набор>` |
| | `DynamicKatanaAnims` | `_DK` | ″ |
| | `DynamicKatanaAnimsV2` | `_K2` | ″ |
| | `DynamicKatanaAnimsV3` | `_KatanaV3` | ″ |
| | `DynamicSwordAndShield` | `_DSAS` | ″ |
| | `DynamicSwordAndShieldV2` | `_WSS2` | ″ |
| | `DynamicSwordAndShieldV3` | `_DSS3` | ″ |
| | `KatanaAnims` | `_Katana` | ″ |
| | `TwinSwordAnims` | — (`SK_Mannequin`) | ″ |

```powershell
$mesh = '/Game/CombatMasterAnimBundle/Demo/Characters/Mannequin_UE4/Meshes/SK_Mannequin'
Invoke-UeScript retarget_pack.py $anim @{ SWORD_RTG_PACK = '/Game/CombatMasterAnimBundle'; SWORD_RTG_INCLUDE = '/PowerfulSwordAnims/'
    SWORD_RTG_SOURCE_MESH = "${mesh}_PowerfulSword"; SWORD_RTG_OUT = '/Game/SWORD/AnimationsCombat/PowerfulSword' }
# … остальные наборы по таблице, затем выгрузка:
Invoke-UeScript export_gasp.py $anim @{ SWORD_EXPORT_CONFIG = "$dir/export_config_combat.json" }
Invoke-UeScript export_gasp.py $anim @{ SWORD_EXPORT_CONFIG = "$dir/export_config_combat_ue4src.json" }
Invoke-UeScript export_gasp.py $anim @{ SWORD_EXPORT_CONFIG = "$dir/export_config_combat_ext.json" }
Invoke-UeScript export_gasp.py $anim @{ SWORD_EXPORT_CONFIG = "$dir/export_config_combat_ext_ue4src.json" }
```

Меши, оружие, анатомия, текстуры:

```powershell
Invoke-UeScript export_meshes.py $gltf
Invoke-UeScript export_weapons.py $gltf
# SK_Skeleton — LOD3 (~12 тыс. вершин), мозг — статика LOD0; под «SK_Skeleton» попадает и SK_SkeletonWithOrgans (LOD4)
Invoke-UeScript export_organs.py $gltf @{ SWORD_ORGANS_ONLY = 'SK_Skeleton;BrainHighPoly' }
# органы — LOD1 (86 226 вершин): бюджет выше, файл второго прогона заменяет LOD4
Invoke-UeScript export_organs.py $gltf @{ SWORD_ORGANS_ONLY = 'SkeletonWithOrgans'; SWORD_ORGANS_BUDGET = '90000' }
Invoke-UeScript export_textures.py $tex                 # кровь → _Export/blood
Invoke-UeScript export_textures.py $tex @{ SWORD_TEX_ROOTS = '/Game/HumanOrgans/Textures'
    SWORD_TEX_OUT = 'D:/Temp/GamedevAssets/_Export/organs/textures'; SWORD_TEX_SKIP = 'Colontex' }
```

`stats.json` и `export.log` у `export_organs.py` переписываются каждым прогоном. Проба без записи: `export_gasp.py`
с `SWORD_EXPORT_DRYRUN=1` (список клипов, кости и кривые первого клипа), `export_organs.py` с `SWORD_ORGANS_STATS=1`
(только `stats.json`). `export_gasp.py` можно запустить и из редактора: Tools → Execute Python Script.

Проверка синтаксиса без Unreal (модуля `unreal` вне UE нет — только компиляция):
`python -m py_compile tools/ue/<скрипт>.py`.

## Переменные окружения

| переменная | скрипт | по умолчанию | что |
|---|---|---|---|
| `SWORD_RTG_SCALE_H` | `retarget_gasp.py` | `1.0` | Pelvis Motion `scale_horizontal` (для опытов) |
| `SWORD_RTG_LEG_BLEND` | `retarget_gasp.py` | `0.0` | Blend to Source ног (для опытов) |
| `SWORD_RTG_ONLY` | `retarget_gasp.py` | — | регулярка отбора клипов (опыт на одном клипе; `/Game/SWORD/GASP_UE5` тогда не очищается) |
| `SWORD_RTG_PACK` | `retarget_pack.py` | `/Game/1HM_Sword_Attacks_Finishers` | корень пака в UE |
| `SWORD_RTG_SUBDIR` | `retarget_pack.py` | `/Animations` | папка клипов внутри пака |
| `SWORD_RTG_INCLUDE` | `retarget_pack.py` | — | подстроки пути через `;` — только совпавшие наборы |
| `SWORD_RTG_SOURCE_MESH` | `retarget_pack.py` | `…/1HM_Sword_Attacks_Finishers/Mannequin/Character/Mesh/SK_Mannequin` | меш исходного скелета |
| `SWORD_RTG_OUT` | `retarget_pack.py` | `/Game/SWORD/AnimationsUE5` | куда класть клипы на UE5 Manny |
| `SWORD_EXPORT_CONFIG` | `export_gasp.py` | `export_config.json` рядом | конфиг выгрузки клипов |
| `SWORD_EXPORT_DRYRUN` | `export_gasp.py` | — | `1` (или аргумент `--dry-run`) — без записи |
| `SWORD_MESH_OUT` | `export_meshes.py` | `EXPORT_ROOT/meshes` | куда писать |
| `SWORD_WEAPON_ROOTS` | `export_weapons.py` | `/Game/CombatMasterAnimBundle/Weapon` | корни UE через `;` |
| `SWORD_WEAPON_OUT` | `export_weapons.py` | `EXPORT_ROOT/weapons` | куда писать |
| `SWORD_ORGANS_ROOT` | `export_organs.py` | `/Game/HumanOrgans` | корень пака в UE |
| `SWORD_ORGANS_OUT` | `export_organs.py` | `EXPORT_ROOT/organs` | куда писать |
| `SWORD_ORGANS_ONLY` | `export_organs.py` | — | подстроки имён через `;` |
| `SWORD_ORGANS_BUDGET` | `export_organs.py` | `14000` | бюджет: вершин у скелетного меша, треугольников у статики |
| `SWORD_ORGANS_STATS` | `export_organs.py` | — | `1` — только `stats.json` |
| `SWORD_TEX_ROOTS` | `export_textures.py` | `/Game/RealisticBlood/_Commons/Textures` | корни UE через `;` |
| `SWORD_TEX_OUT` | `export_textures.py` | `EXPORT_ROOT/blood` | куда писать |
| `SWORD_TEX_SKIP` | `export_textures.py` | — | подстроки имён через `;`, которые не выгружать (экспортёр падает на них) |

## Что решается здесь, а что — в пекарне

Только здесь (нужен Unreal):
- ретаргет — настройки ретаргетеров (ноги, таз, цепочки, поза ретаргета, пястные);
- отбор клипов на выгрузке — `root`, `include`, `exclude`, `maxClips` конфига (у GASP ещё `export_config.json` на ретаргете);
- частота `sampleFps` — одна на выгрузку (пекарня требует одну частоту у всех клипов выгрузки и не передискретизирует);
- флаг цикла — `loopRegex` по имени клипа;
- кости и иерархия (`bones`, `parentsMesh`), кривые (`curves`), sync markers и notifies;
- LOD мешей анатомии (`SWORD_ORGANS_BUDGET`), состав текстур (`SWORD_TEX_ROOTS`, `SWORD_TEX_SKIP`).

В пекарне (манифесты `Bake/` и код обработчиков): корень — источник root motion (`root` или таз), дорожки смещения и
курса, дрейф цикла, система первого кадра; оси UE → SWORD; пальцы (`fingerSrc`); наборы (`sets`: каждый клип выгрузки —
ровно в один набор по папке UE); прореживание мешей (`maxTriangles`), сжатие текстур, перенос анатомии в позу рига.
Обрезки краёв, зеркалирования, пропуска клипа выгрузки и разметки контактов в пекарне нет (добавлять — кодом
`Bake.Engine`); отбор клипов для Motion Matching — в игре (модуль Locomotion).

## Ограничения и известные ошибки

- Логика скриптов — как в SWORD; изменения переноса — только пути (`EXPORT_ROOT`, `HERE`) и комментарии.
- `export_gasp.py`: файл клипа — `<имя ассета>.bin`, одноимённые клипы из разных папок одной выгрузки перезапишут друг
  друга.
- `export_meshes.py`: поле `parent` костей в `skeleton.json` пустое (в UE 5.8 у `AnimPoseExtensions` нет
  `get_parent_bone_name`); пекарня берёт позу покоя по имени кости, иерархию — из `manifest.json` выгрузки клипов.
- `retarget_pack.py`: путь копии строится заменой `<пак>/Animations` на `SWORD_RTG_OUT`, поэтому с `SWORD_RTG_SUBDIR`
  не `/Animations` копии пишутся в папки самого пака. С `SWORD_RTG_OUT` на набор папка набора повторяется
  (`…/PowerfulSword/PowerfulSwordAnims/…`) — наборы пекарня различает по папке, это не мешает.
- `export_organs.py` меняет UE-проект: досозданные LOD сохраняются в ассет.
- В SWORD2 нет разовых исследований SWORD (`probe_*.py`, `retarget_variants.py`) и `export_config_sword.json` (выгрузка
  1HM после старого ретаргета на UEFN в `_Export/sword`): его никто не зовёт, `_Export/sword` не читает ни один
  манифест. Остатки тех опытов в UE-проекте (`/Game/SWORD/Animations`, `/Game/SWORD/RetargetVariants`) конвейер не
  читает.

## Агентские инструменты

Скрипты выполняются в Unreal вне SWORD2 — агентский вид не нужен. Выход проверяется выпечкой (лог обработчиков:
сверка костей выгрузки с ригом, FK против выгрузки) и `bake diff` с эталонным паком (точка Б3). В SWORD2 проверяются
синтаксис (`python -m py_compile`) и переводы строк (шаг `line-endings` `verify.ps1`, `.py` в его списке).
