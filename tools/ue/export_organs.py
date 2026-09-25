"""
Экспорт анатомии (пак HumanOrgans, скелет UE4 Mannequin) из рабочей копии GASP (UE 5.8) в glTF —
исходники обработчика пекарни. Скелет идёт скинированным мешем (веса по костям), органы — статикой.

Меши пака плотные (скелет — десятки МБ), поэтому перед экспортом при необходимости досоздаются
LOD-ы и берётся первый, который влезает в бюджет треугольников (SWORD_ORGANS_BUDGET). Статику в commandlet
не упростить (подсистемы статики нет, EditorStaticMeshLibrary.set_lods отвечает −1): она уходит LOD0, а
прореживает её пекарня (anatomy.mesh, maxTriangles).

  SWORD_ORGANS_ROOT    корень пака в UE (по умолчанию /Game/HumanOrgans)
  SWORD_ORGANS_OUT     куда класть .glb (по умолчанию EXPORT_ROOT/organs)
  SWORD_ORGANS_ONLY    подстроки имён через «;» — экспортировать только их
  SWORD_ORGANS_BUDGET  бюджет на меш: вершин у скелетного, треугольников у статики (по умолчанию 14000)
  SWORD_ORGANS_STATS   1 — только посчитать и записать stats.json, без экспорта

Запуск: -run=pythonscript -EnablePlugins=PythonScriptPlugin,EditorScriptingUtilities,GLTFExporter (команда — tools/ue/README.md)
"""
import json
import os

import unreal

# Путь к исходникам на этой машине — только здесь: корень выгрузок вне репозитория (${src}/_Export пекарни).
EXPORT_ROOT = "D:/Temp/GamedevAssets/_Export"
ROOT = os.environ.get("SWORD_ORGANS_ROOT") or "/Game/HumanOrgans"
OUT = os.environ.get("SWORD_ORGANS_OUT") or EXPORT_ROOT + "/organs"
ONLY = [s for s in (os.environ.get("SWORD_ORGANS_ONLY") or "").split(";") if s]
BUDGET = int(os.environ.get("SWORD_ORGANS_BUDGET") or "14000")
STATS_ONLY = os.environ.get("SWORD_ORGANS_STATS") == "1"

eal = unreal.EditorAssetLibrary


_LOG = None


def log(m):
    # в commandlet вывод unreal.log до консоли доходит не всегда — копия в OUT/export.log
    global _LOG
    unreal.log("[sword-organs] " + m)
    if _LOG is None:
        os.makedirs(OUT, exist_ok=True)
        _LOG = open(os.path.join(OUT, "export.log"), "w", encoding="utf-8")
    _LOG.write(m + "\n")
    _LOG.flush()


def subsystem(cls):
    try:
        return unreal.get_editor_subsystem(cls)
    except Exception as e:
        log(f"нет подсистемы {cls}: {e}")
        return None


SKEL = subsystem(unreal.SkeletalMeshEditorSubsystem)
STATIC = subsystem(unreal.StaticMeshEditorSubsystem)


def lod_count(asset, skeletal):
    try:
        return SKEL.get_lod_count(asset) if skeletal else asset.get_num_lods()
    except Exception:
        try:
            return asset.get_editor_property("lod_num")
        except Exception:
            return 1


def tris(asset, lod, skeletal):
    """Мера размера LOD: у скелетного меша — вершины (треугольников подсистема не даёт), у статики — треугольники."""
    try:
        if skeletal:
            return int(SKEL.get_num_verts(asset, lod))
        return int(STATIC.get_number_triangles(asset, lod))
    except Exception:
        return -1


def pick_lod(asset, skeletal, name):
    """LOD под бюджет: если у меша их мало, пробуем досоздать (только для скелетных — у статики свои настройки)."""
    if not skeletal and STATIC is None:
        # в commandlet треугольники статики не посчитать и LOD не построить — LOD0, прореживает пекарня
        log(f"{name}: статика — LOD0 (прореживает пекарня, maxTriangles)")
        return 0, -1, []
    n = lod_count(asset, skeletal)
    counts = [tris(asset, i, skeletal) for i in range(n)]
    log(f"{name}: LOD-ов {n}, размеры {counts}")
    for i, c in enumerate(counts):
        if 0 < c <= BUDGET:
            return i, c, counts
    if skeletal and SKEL is not None and n < 5:
        try:
            SKEL.regenerate_lod(asset, 5, regenerate_even_if_imported=True, generate_base_lod=False)
            eal.save_loaded_asset(asset)
            n = lod_count(asset, skeletal)
            counts = [tris(asset, i, skeletal) for i in range(n)]
            log(f"{name}: досоздано LOD-ов {n}, размеры {counts}")
            for i, c in enumerate(counts):
                if 0 < c <= BUDGET:
                    return i, c, counts
        except Exception as e:
            log(f"{name}: не вышло досоздать LOD ({e})")
    return n - 1, counts[-1] if counts else -1, counts


def options(lod):
    opts = unreal.GLTFExportOptions()
    for name, value in (("export_uniform_scale", 1.0), ("export_vertex_skin_weights", True),
                        ("export_proxy_materials", False), ("default_level_of_detail", lod),
                        ("bake_material_inputs", unreal.GLTFMaterialBakeMode.USE_MESH_DATA)):
        try:
            opts.set_editor_property(name, value)
        except Exception as e:
            log(f"опция {name}: {e}")
    return opts


def main():
    os.makedirs(OUT, exist_ok=True)
    stats = {}
    for path in eal.list_assets(ROOT, recursive=True, include_folder=False):
        data = eal.find_asset_data(path)
        kind = str(data.asset_class_path.asset_name)
        if kind not in ("SkeletalMesh", "StaticMesh"):
            continue
        name = str(data.asset_name)
        if ONLY and not any(s.lower() in name.lower() for s in ONLY):
            continue
        asset = eal.load_asset(path)
        if asset is None:
            log(f"НЕ ЗАГРУЗИЛСЯ {path}")
            continue
        skeletal = kind == "SkeletalMesh"
        lod, count, counts = pick_lod(asset, skeletal, name)
        entry = {"path": path, "kind": kind, "lod": lod, "tris": count, "all_lods": counts}
        try:
            box = asset.get_bounds().box
            entry["bounds"] = {"min": [box.min.x, box.min.y, box.min.z], "max": [box.max.x, box.max.y, box.max.z]}
        except Exception:
            pass
        if skeletal:
            try:
                entry["skeleton"] = asset.skeleton.get_path_name()
            except Exception:
                pass
        if not STATS_ONLY:
            dst = os.path.join(OUT, name + ".glb")
            res = unreal.GLTFExporter.export_to_gltf(asset, dst, options(lod), set())
            ok = bool(res[0] if isinstance(res, tuple) else res)
            entry["file"] = os.path.basename(dst) if ok else None
            entry["bytes"] = os.path.getsize(dst) if ok and os.path.exists(dst) else 0
            log(f"{name}: LOD{lod}, ~{count} тр. → {'ok' if ok else 'ОШИБКА'} ({entry['bytes']} байт)")
        stats[name] = entry
    with open(os.path.join(OUT, "stats.json"), "w", encoding="utf-8") as f:
        json.dump(stats, f, ensure_ascii=False, indent=1)
    log(f"готово: мешей {len(stats)} → {OUT}")


main()
