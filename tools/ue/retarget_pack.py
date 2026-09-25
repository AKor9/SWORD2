"""
Ретаргет пака на скелете UE4 Mannequin (по умолчанию 1HM_Sword_Attacks_Finishers) на UE5 Manny — скелет персонажа SWORD.
Запуск в РАБОЧЕЙ КОПИИ GASP (D:/Temp/GamedevAssets/GameAnimationSample_SWORD), не в скачанном проекте;
полная команда — tools/ue/README.md.

Создаёт /Game/SWORD/Retarget/RTG_UE4_to_UE5 (источник IK_UE4_Mannequin_Retarget, цель IK_UE5_Mannequin_Retarget,
цепочки — автосопоставление) и копии клипов на UE5 Manny в SWORD_RTG_OUT (по умолчанию /Game/SWORD/AnimationsUE5)
с папками пака. Повторный запуск пересоздаёт результат.
"""
import os

import unreal

# Пак задаётся переменными окружения — скрипт один на все паки на скелете UE4 Mannequin.
# По умолчанию — 1HM_Sword_Attacks_Finishers, как было.
#   SWORD_RTG_PACK        корень пака в UE, например /Game/CombatMasterAnimBundle
#   SWORD_RTG_SUBDIR      папка с клипами внутри пака (по умолчанию /Animations)
#   SWORD_RTG_OUT         куда положить клипы на UE5 Manny
#   SWORD_RTG_SOURCE_MESH меш исходного скелета пака (нужен ретаргетеру)
PACK = os.environ.get("SWORD_RTG_PACK", "/Game/1HM_Sword_Attacks_Finishers")
ANIMS = PACK + os.environ.get("SWORD_RTG_SUBDIR", "/Animations")
# Цель — стандартный скелет UE5 Manny (SK_Mannequin): под него продаются паки на Fab.
OUT = os.environ.get("SWORD_RTG_OUT", "/Game/SWORD/AnimationsUE5")
RTG_DIR = "/Game/SWORD/Retarget"
RTG_NAME = "RTG_UE4_to_UE5"

SOURCE_RIG = "/Game/Characters/UE4_Mannequin/Rigs/IK_UE4_Mannequin_Retarget"
TARGET_RIG = "/Game/Characters/UE5_Mannequins/Rigs/IK_UE5_Mannequin_Retarget"
SOURCE_MESH = os.environ.get(
    "SWORD_RTG_SOURCE_MESH", "/Game/1HM_Sword_Attacks_Finishers/Mannequin/Character/Mesh/SK_Mannequin")
TARGET_MESH = "/Game/Characters/UE5_Mannequins/Meshes/SKM_Manny"

eal = unreal.EditorAssetLibrary


def log(m):
    unreal.log("[sword-rtg] " + m)


def load(path):
    a = eal.load_asset(path)
    if a is None:
        raise RuntimeError(f"не загрузился {path}")
    return a


def make_retargeter():
    path = f"{RTG_DIR}/{RTG_NAME}"
    # без окна пересоздать существующий ассет нельзя — существующий настраивается заново
    rtg = eal.load_asset(path) if eal.does_asset_exist(path) else None
    if rtg is None:
        tools = unreal.AssetToolsHelpers.get_asset_tools()
        rtg = tools.create_asset(RTG_NAME, RTG_DIR, unreal.IKRetargeter, unreal.IKRetargetFactory())
    if rtg is None:
        raise RuntimeError(f"не удалось создать или загрузить {path}")
    c = unreal.IKRetargeterController.get_controller(rtg)
    src_rig, tgt_rig = load(SOURCE_RIG), load(TARGET_RIG)
    c.set_ik_rig(unreal.RetargetSourceOrTarget.SOURCE, src_rig)
    c.set_ik_rig(unreal.RetargetSourceOrTarget.TARGET, tgt_rig)
    try:
        c.set_preview_mesh(unreal.RetargetSourceOrTarget.SOURCE, load(SOURCE_MESH))
        c.set_preview_mesh(unreal.RetargetSourceOrTarget.TARGET, load(TARGET_MESH))
    except Exception as e:
        log(f"превью-меши не заданы: {e}")
    # стек операций 5.6+: стандартный набор (пелвис, FK-цепочки, IK ног, root motion)
    try:
        c.remove_all_ops()
        c.add_default_ops()
        c.assign_ik_rig_to_all_ops(unreal.RetargetSourceOrTarget.SOURCE, src_rig)
        c.assign_ik_rig_to_all_ops(unreal.RetargetSourceOrTarget.TARGET, tgt_rig)
        log(f"операций в стеке: {c.get_num_retarget_ops()}: " +
            ", ".join(str(c.get_op_name(i)) for i in range(c.get_num_retarget_ops())))
    except Exception as e:
        log(f"стек операций: {e}")
    try:
        c.auto_map_chains(unreal.AutoMapChainType.FUZZY, True)
    except Exception as e:
        log(f"автосопоставление цепочек: {e}")
    # Поза ретаргета: кости цели выравниваются по направлениям источника. Без этого разница поз покоя
    # UE4 и UEFN (у UEFN ещё и пястные кости) уходила в каждый кадр — пальцы гнулись на 20–40° сильнее
    # пака, левая кисть скрючивалась.
    try:
        c.auto_align_all_bones(unreal.RetargetSourceOrTarget.TARGET)
        log("поза ретаргета цели выровнена по источнику")
    except Exception as e:
        log(f"выравнивание позы ретаргета: {e}")
    # У UE4 нет пястных костей — выравнивание разворачивало их назад (сгиб кисть→пясть 140–170°),
    # пальцы уходили крюком. Пястные возвращаются в позу меша.
    try:
        side = unreal.RetargetSourceOrTarget.TARGET
        meta = [unreal.Name(f"{f}_metacarpal_{s}") for f in ("index", "middle", "ring", "pinky") for s in ("l", "r")]
        c.reset_retarget_pose(c.get_current_retarget_pose_name(side), meta, side)
        log(f"пястные кости сброшены: {len(meta)}")
    except Exception as e:
        log(f"сброс пястных: {e}")
    eal.save_loaded_asset(rtg)
    log(f"ретаргетер {path} создан")
    return rtg


def main():
    rtg = make_retargeter()
    # SWORD_RTG_INCLUDE — подстроки пути через «;»: ретаргетятся только совпавшие наборы
    # (в паке бывает по десятку наборов под разное оружие, а нужен один).
    include = [s for s in os.environ.get("SWORD_RTG_INCLUDE", "").split(";") if s]
    seqs = [p for p in eal.list_assets(ANIMS, recursive=True, include_folder=False)
            if str(eal.find_asset_data(p).asset_class_path.asset_name) == "AnimSequence"
            and (not include or any(s in p for s in include))]
    log(f"клипов для ретаргета: {len(seqs)}" + (f" (фильтр: {', '.join(include)})" if include else ""))
    if not seqs:
        raise RuntimeError(f"в {ANIMS} нет подходящих клипов" + (f" по фильтру {include}" if include else ""))

    if eal.does_directory_exist(OUT):
        eal.delete_directory(OUT)

    # по папкам пака, чтобы сохранить структуру: replace «/Game/1HM…/Animations» → OUT
    folders = {}
    for p in seqs:
        folders.setdefault(p.rsplit("/", 1)[0], []).append(p)
    total = 0
    for folder, paths in sorted(folders.items()):
        sub = folder.replace(f"{PACK}/Animations", OUT)
        assets = [unreal.EditorAssetLibrary.find_asset_data(p) for p in paths]
        # в 5.8 помечено устаревшим (RunBatchRetarget), но работает и не требует структуры входов
        new = unreal.IKRetargetBatchOperation.duplicate_and_retarget(
            assets, load(SOURCE_MESH), load(TARGET_MESH), rtg,
            target_path=sub, use_source_path=False, include_referenced_assets=False, overwrite_existing_files=True)
        moved = sum(1 for d in new if isinstance(d.get_asset(), unreal.AnimSequence))
        total += moved
        log(f"{sub}: {moved} из {len(paths)}")
    eal.save_directory(OUT, only_if_is_dirty=False, recursive=True)
    eal.save_directory(RTG_DIR, only_if_is_dirty=False, recursive=True)
    log(f"готово: {total} клипов на UE5 Manny в {OUT}")


main()
