"""
Экспорт статических мешей оружия из рабочей копии GASP (UE 5.8) в glTF — исходники обработчика weapon.static пекарни.
Все StaticMesh в каталогах SWORD_WEAPON_ROOTS (через «;», по умолчанию /Game/CombatMasterAnimBundle/Weapon)
→ SWORD_WEAPON_OUT/<имя ассета>.glb (по умолчанию EXPORT_ROOT/weapons; вне git: лицензия Epic).
Запуск: -run=pythonscript -EnablePlugins=PythonScriptPlugin,EditorScriptingUtilities,GLTFExporter (команда — tools/ue/README.md)
"""
import os

import unreal

# Путь к исходникам на этой машине — только здесь: корень выгрузок вне репозитория (${src}/_Export пекарни).
EXPORT_ROOT = "D:/Temp/GamedevAssets/_Export"
OUT = os.environ.get("SWORD_WEAPON_OUT") or EXPORT_ROOT + "/weapons"
ROOTS = (os.environ.get("SWORD_WEAPON_ROOTS") or "/Game/CombatMasterAnimBundle/Weapon").split(";")

eal = unreal.EditorAssetLibrary


def log(m):
    unreal.log("[sword-weapon] " + m)


os.makedirs(OUT, exist_ok=True)
opts = unreal.GLTFExportOptions()
for name, value in (("export_uniform_scale", 1.0), ("export_proxy_materials", False),
                    ("bake_material_inputs", unreal.GLTFMaterialBakeMode.USE_MESH_DATA)):
    try:
        opts.set_editor_property(name, value)
    except Exception as e:
        log(f"опция {name}: {e}")
count = 0
for root in ROOTS:
    for p in eal.list_assets(root, recursive=True, include_folder=False):
        if str(eal.find_asset_data(p).asset_class_path.asset_name) != "StaticMesh":
            continue
        asset = eal.load_asset(p)
        dst = os.path.join(OUT, asset.get_name() + ".glb")
        res = unreal.GLTFExporter.export_to_gltf(asset, dst, opts, set())
        ok = bool(res[0] if isinstance(res, tuple) else res)
        log(f"{p} → {dst}: {'ok' if ok else 'ОШИБКА'}")
        count += int(ok)
log(f"готово: {count} мешей → {OUT}")
