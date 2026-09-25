"""
Экспорт мешей для скелетных персонажей SWORD из рабочей копии GASP (UE 5.8):
  - SKM_Manny (UE5 Manny: скелет + скин-веса + материалы) → manny.glb;
  - одноручный меч пака 1HM → sword_1h.glb;
  - skeleton.json: кости UE5 Manny с позой покоя (parent в UE 5.8 пуст), сокеты (крепление меча).
Запуск: -run=pythonscript -EnablePlugins=PythonScriptPlugin,EditorScriptingUtilities,GLTFExporter
Выход — SWORD_MESH_OUT или EXPORT_ROOT/meshes (вне git: лицензия Epic). Команда — tools/ue/README.md.
"""
import json
import os

import unreal

# Путь к исходникам на этой машине — только здесь: корень выгрузок вне репозитория (${src}/_Export пекарни).
EXPORT_ROOT = "D:/Temp/GamedevAssets/_Export"
OUT = os.environ.get("SWORD_MESH_OUT") or EXPORT_ROOT + "/meshes"
ASSETS = {
    "manny.glb": "/Game/Characters/UE5_Mannequins/Meshes/SKM_Manny",
    "sword_1h.glb": "/Game/1HM_Sword_Attacks_Finishers/Weapons/OneHandSword_Mesh",
}
SKELETONS = {
    "character": "/Game/Characters/UE5_Mannequins/Meshes/SK_Mannequin",
    "pack_ue4": "/Game/1HM_Sword_Attacks_Finishers/Mannequin/Character/Mesh/UE4_Mannequin_Skeleton",
}
MESH_FOR_POSE = "/Game/Characters/UE5_Mannequins/Meshes/SKM_Manny"

eal = unreal.EditorAssetLibrary


def log(m):
    unreal.log("[sword-mesh] " + m)


def xf(t):
    return {"t": [t.translation.x, t.translation.y, t.translation.z],
            "q": [t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w],
            "s": [t.scale3d.x, t.scale3d.y, t.scale3d.z]}


def export_gltf():
    exporter = getattr(unreal, "GLTFExporter", None)
    if exporter is None:
        log("НЕТ unreal.GLTFExporter — включите плагин GLTFExporter")
        return
    opts = unreal.GLTFExportOptions()
    for name, value in (("export_uniform_scale", 1.0), ("export_vertex_skin_weights", True),
                        ("export_proxy_materials", False), ("bake_material_inputs", unreal.GLTFMaterialBakeMode.USE_MESH_DATA)):
        try:
            opts.set_editor_property(name, value)
        except Exception as e:
            log(f"опция {name}: {e}")
    for file, path in ASSETS.items():
        asset = eal.load_asset(path)
        if asset is None:
            log(f"НЕ ЗАГРУЗИЛСЯ {path}")
            continue
        dst = os.path.join(OUT, file)
        ok = exporter.export_to_gltf(asset, dst, opts, set())
        log(f"{file}: {'ok' if ok else 'ОШИБКА'} ({os.path.getsize(dst) if os.path.exists(dst) else 0} байт)")


def export_skeletons():
    data = {}
    mesh = eal.load_asset(MESH_FOR_POSE)
    for key, path in SKELETONS.items():
        sk = eal.load_asset(path)
        if sk is None:
            log(f"НЕ ЗАГРУЗИЛСЯ {path}")
            continue
        entry = {"path": path, "sockets": []}
        try:
            for s in sk.get_editor_property("sockets"):
                entry["sockets"].append({
                    "name": str(s.get_editor_property("socket_name")),
                    "bone": str(s.get_editor_property("bone_name")),
                    "t": [s.relative_location.x, s.relative_location.y, s.relative_location.z],
                    "r": [s.relative_rotation.roll, s.relative_rotation.pitch, s.relative_rotation.yaw],
                    "s": [s.relative_scale.x, s.relative_scale.y, s.relative_scale.z],
                })
        except Exception as e:
            log(f"{key}: сокеты недоступны ({e})")
        data[key] = entry
    if mesh is not None:
        ref = unreal.AnimPoseExtensions.get_reference_pose(mesh.skeleton) if hasattr(unreal.AnimPoseExtensions, "get_reference_pose") else None
        bones = []
        if ref is not None:
            names = [str(b) for b in unreal.AnimPoseExtensions.get_bone_names(ref)]
            for n in names:
                parent = str(unreal.AnimPoseExtensions.get_parent_bone_name(ref, n)) if hasattr(unreal.AnimPoseExtensions, "get_parent_bone_name") else ""
                bones.append({"name": n, "parent": parent,
                              "local": xf(unreal.AnimPoseExtensions.get_bone_pose(ref, n, unreal.AnimPoseSpaces.LOCAL)),
                              "world": xf(unreal.AnimPoseExtensions.get_bone_pose(ref, n, unreal.AnimPoseSpaces.WORLD))})
        data["character"]["bones"] = bones
        mesh_sockets = []
        try:
            for i in range(mesh.num_sockets()):
                s = mesh.get_socket_by_index(i)
                mesh_sockets.append({"name": str(s.socket_name), "bone": str(s.bone_name),
                                     "t": [s.relative_location.x, s.relative_location.y, s.relative_location.z],
                                     "r": [s.relative_rotation.roll, s.relative_rotation.pitch, s.relative_rotation.yaw]})
        except Exception as e:
            log(f"сокеты меша недоступны ({e})")
        data["character"]["mesh_sockets"] = mesh_sockets
        log(f"костей персонажа: {len(bones)}, сокетов скелета: {len(data['character']['sockets'])}, меша: {len(mesh_sockets)}")
    with open(os.path.join(OUT, "skeleton.json"), "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=1)


def dump_pack_blueprints():
    """Блюпринты пака (персонаж с мечом) — ищем, к какой кости и с каким смещением крепится меч."""
    for p in eal.list_assets("/Game/1HM_Sword_Attacks_Finishers", recursive=True, include_folder=False):
        if str(eal.find_asset_data(p).asset_class_path.asset_name) != "Blueprint":
            continue
        bp = eal.load_asset(p)
        log(f"blueprint {p}")
        try:
            gen = unreal.EditorAssetLibrary.load_blueprint_class(p)
            cdo = unreal.get_default_object(gen)
            for comp in cdo.get_components_by_class(unreal.SceneComponent):
                parent = comp.get_attach_parent()
                log(f"  {comp.get_name()} ({comp.get_class().get_name()}) socket={comp.get_attach_socket_name()} "
                    f"parent={parent.get_name() if parent else None} rel={comp.get_relative_transform()}")
        except Exception as e:
            log(f"  компоненты недоступны: {e}")


os.makedirs(OUT, exist_ok=True)
export_gltf()
export_skeletons()
dump_pack_blueprints()
log("готово → " + OUT)
