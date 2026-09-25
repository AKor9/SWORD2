"""
Ретаргет локомоции Game Animation Sample (скелет UEFN Mannequin) на UE5 Manny — скелет персонажа SWORD.
Запуск в РАБОЧЕЙ КОПИИ GASP (D:/Temp/GamedevAssets/GameAnimationSample_SWORD; полная команда — tools/ue/README.md):
  UnrealEditor-Cmd.exe <uproject> -run=pythonscript -script="<путь>/retarget_gasp.py"
      -EnablePlugins=PythonScriptPlugin,EditorScriptingUtilities -nullrhi -unattended -nosplash -nopause -stdout

Ретаргетер — копия RTG_UEFN_to_UE5_Mannequin из GASP (оригинал не меняется) с поправкой Blend to Source:
у ног BlendToSource = 0 (LEG_BLEND_TO_SOURCE). В оригинале цели стоп берутся из исходника (UEFN) и отстают от
таза Manny, которого Pelvis Motion масштабирует на рост (1.033): опора скользит на 3%, точки приземления уползают
назад на 6.6 см за шаг, циклы не замыкаются (аудит SWORD: Sword.exe --loco-audit). Масштаб таза этого не лечит —
цели ног идут за ним. Переменные SWORD_RTG_SCALE_H, SWORD_RTG_LEG_BLEND — для опытов, SWORD_RTG_ONLY — регулярка
отбора клипов (опыт на одном клипе; OUT тогда не очищается). Клипы отбираются теми же
регулярками, что в export_config.json (локомоция для Motion Matching), копии — в
/Game/SWORD/GASP_UE5/<папка GASP>/. Повторный запуск пересоздаёт результат.
"""
import json
import os
import re

import unreal

# Папка скрипта: рядом лежит export_config.json (отбор клипов). В commandlet __file__ бывает не задан — тогда
# tools/ue основного дерева SWORD2.
HERE = os.path.dirname(os.path.abspath(__file__)) if "__file__" in globals() else "D:/Work/Visual Studio/SWORD2/tools/ue"
OUT = "/Game/SWORD/GASP_UE5"
RTG_GASP = "/Game/Characters/UE5_Mannequins/Rigs/RTG_UEFN_to_UE5_Mannequin"
RTG = "/Game/SWORD/Rigs/RTG_UEFN_to_UE5_Mannequin_SWORD"
RTG_SCALE_H = float(os.environ.get("SWORD_RTG_SCALE_H", "1.0"))
LEG_BLEND_TO_SOURCE = float(os.environ.get("SWORD_RTG_LEG_BLEND", "0.0"))
ONLY = os.environ.get("SWORD_RTG_ONLY")
SOURCE_MESH = "/Game/Characters/UEFN_Mannequin/Meshes/SKM_UEFN_Mannequin"
TARGET_MESH = "/Game/Characters/UE5_Mannequins/Meshes/SKM_Manny"

eal = unreal.EditorAssetLibrary


def log(m):
    unreal.log("[sword-rtg-gasp] " + m)


def load(path):
    a = eal.load_asset(path)
    if a is None:
        raise RuntimeError(f"не загрузился {path}")
    return a


def make_rtg():
    """
    Копия ретаргетера GASP с поправками. Есть — перенастраивается (удалить нельзя: на неё ссылаются
    ретаргетнутые клипы), нет — копируется из GASP. Все правленые значения задаются явно.
    """
    rtg = eal.load_asset(RTG) if eal.does_asset_exist(RTG) else eal.duplicate_asset(RTG_GASP, RTG)
    if rtg is None:
        raise RuntimeError(f"не скопировался {RTG_GASP}")
    ctrl = unreal.IKRetargeterController.get_controller(rtg)
    i = ctrl.get_index_of_op_by_name("Pelvis Motion")
    if i < 0:
        raise RuntimeError("в ретаргетере нет операции Pelvis Motion")
    op = ctrl.get_op_controller(i)
    s = op.get_settings()
    log(f"Pelvis Motion: scale_horizontal {s.scale_horizontal} -> {RTG_SCALE_H:.5f}")
    s.scale_horizontal = RTG_SCALE_H
    op.set_settings(s)
    if abs(op.get_settings().scale_horizontal - RTG_SCALE_H) > 1e-5:
        raise RuntimeError("scale_horizontal не записался")

    i = ctrl.get_index_of_op_by_name("Blend to Source")
    if i < 0:
        raise RuntimeError("в ретаргетере нет операции Blend to Source")
    op = ctrl.get_op_controller(i)
    s = op.get_settings()
    chains = list(s.chains)
    for c in chains:
        if str(c.target_chain_name) in ("LeftLeg", "RightLeg"):
            log(f"Blend to Source {c.target_chain_name}: blend_to_source {c.blend_to_source} -> {LEG_BLEND_TO_SOURCE}")
            c.blend_to_source = LEG_BLEND_TO_SOURCE
    s.chains = chains
    op.set_settings(s)
    got = [(str(c.target_chain_name), c.blend_to_source) for c in op.get_settings().chains]
    log(f"Blend to Source: {got}")
    if any(n in ("LeftLeg", "RightLeg") and abs(b - LEG_BLEND_TO_SOURCE) > 1e-5 for n, b in got):
        raise RuntimeError("blend_to_source ног не записался")
    eal.save_asset(RTG, only_if_is_dirty=False)
    return rtg


def main():
    with open(os.path.join(HERE, "export_config.json"), "r", encoding="utf-8") as f:
        cfg = json.load(f)
    include = [re.compile(p) for p in cfg["include"]]
    exclude = [re.compile(p) for p in cfg["exclude"]]
    root = cfg["root"]
    seqs = []
    for p in sorted(eal.list_assets(root, recursive=True, include_folder=False)):
        if include and not any(r.search(p) for r in include):
            continue
        if any(r.search(p) for r in exclude):
            continue
        if ONLY and not re.search(ONLY, p):
            continue
        if str(eal.find_asset_data(p).asset_class_path.asset_name) == "AnimSequence":
            seqs.append(p.split(".")[0])
    log(f"клипов для ретаргета: {len(seqs)}")

    rtg, src, tgt = make_rtg(), load(SOURCE_MESH), load(TARGET_MESH)
    if not ONLY and eal.does_directory_exist(OUT):
        eal.delete_directory(OUT)

    folders = {}
    for p in seqs:
        folders.setdefault(p.rsplit("/", 1)[0], []).append(p)
    total = 0
    for folder, paths in sorted(folders.items()):
        sub = folder.replace(root, OUT)
        assets = [eal.find_asset_data(p) for p in paths]
        new = unreal.IKRetargetBatchOperation.duplicate_and_retarget(
            assets, src, tgt, rtg, target_path=sub, use_source_path=False,
            include_referenced_assets=False, overwrite_existing_files=True)
        moved = sum(1 for d in new if isinstance(d.get_asset(), unreal.AnimSequence))
        total += moved
        log(f"{sub}: {moved} из {len(paths)}")
    eal.save_directory(OUT, only_if_is_dirty=False, recursive=True)
    log(f"готово: {total} клипов на UE5 Manny в {OUT}")


main()
