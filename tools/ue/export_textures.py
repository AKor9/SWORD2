"""
Экспорт текстур из рабочей копии GASP (UE 5.8) в PNG — исходники для текстурных обработчиков пекарни.
Все Texture2D в каталогах SWORD_TEX_ROOTS (через «;», по умолчанию /Game/RealisticBlood/_Commons/Textures)
→ SWORD_TEX_OUT/<путь относительно корня>/<имя>.png (по умолчанию EXPORT_ROOT/blood; вне git: лицензия).
Рядом пишется textures.json: размер, sRGB, тип сжатия UE — чтобы пекарня знала, где цвет, а где нормали.
SWORD_TEX_SKIP — подстроки имён через «;», которые не выгружать (HumanOrgans: Colontex — текстура анимации
вершин, на ней экспортёр падает). SWORD_TEX_ONLY — имена ассетов через «;» (целиком, без учёта регистра): выгрузить
только их (огонь: четыре текстуры из общей папки битмапов FluidNinja). Из Git Bash корни UE передавать
с MSYS_NO_PATHCONV=1, иначе /Game/… станет путём Windows.
Запуск: -run=pythonscript -EnablePlugins=PythonScriptPlugin,EditorScriptingUtilities (команда — tools/ue/README.md)
"""
import json
import os

import unreal

# Путь к исходникам на этой машине — только здесь: корень выгрузок вне репозитория (${src}/_Export пекарни).
EXPORT_ROOT = "D:/Temp/GamedevAssets/_Export"
OUT = os.environ.get("SWORD_TEX_OUT") or EXPORT_ROOT + "/blood"
ROOTS = (os.environ.get("SWORD_TEX_ROOTS") or "/Game/RealisticBlood/_Commons/Textures").split(";")
SKIP = [s for s in (os.environ.get("SWORD_TEX_SKIP") or "").split(";") if s]
ONLY = {s.lower() for s in (os.environ.get("SWORD_TEX_ONLY") or "").split(";") if s}

eal = unreal.EditorAssetLibrary


def log(m):
    unreal.log("[sword-tex] " + m)


def exporter(ext):
    for cls in ("TextureExporterPNG", "TextureExporterTGA"):
        c = getattr(unreal, cls, None)
        if c is not None and (ext in cls.lower()):
            return c()
    return None


meta = {}
count = 0
found = set()
for root in ROOTS:
    root = root.rstrip("/")
    for p in eal.list_assets(root, recursive=True, include_folder=False):
        if str(eal.find_asset_data(p).asset_class_path.asset_name) != "Texture2D":
            continue
        rel = p[len(root):].lstrip("/").rsplit(".", 1)[0]
        if ONLY and rel.rsplit("/", 1)[-1].lower() not in ONLY:
            continue
        # экспортёр падает (assert SupportsTexture) на текстурах неподдержанного формата — их пропускаем по имени
        if any(s.lower() in rel.lower() for s in SKIP):
            log(f"{p}: пропущена (SWORD_TEX_SKIP)")
            continue
        tex = eal.load_asset(p)
        ok = False
        for ext in ("png", "tga"):
            # в UE 5.8 классы экспортёров текстур из Python не видны — тогда UE выберет экспортёр по расширению сам
            ex = exporter(ext)
            dst = os.path.join(OUT, rel + "." + ext)
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            task = unreal.AssetExportTask()
            task.object = tex
            task.filename = dst
            if ex is not None:
                task.exporter = ex
            task.automated = True
            task.prompt = False
            task.replace_identical = True
            if unreal.Exporter.run_asset_export_task(task) and os.path.exists(dst):
                ok = True
                meta[rel.replace("\\", "/") + "." + ext] = {
                    "size": [tex.blueprint_get_size_x(), tex.blueprint_get_size_y()],
                    "srgb": bool(tex.get_editor_property("srgb")),
                    "compression": str(tex.get_editor_property("compression_settings")),
                }
                break
        log(f"{p}: {'ok' if ok else 'ОШИБКА'}")
        count += int(ok)
        found.add(rel.rsplit("/", 1)[-1].lower())
with open(os.path.join(OUT, "textures.json"), "w", encoding="utf-8") as f:
    json.dump(meta, f, ensure_ascii=False, indent=2)
for name in sorted(ONLY - found):
    log(f"{name}: нет в {ROOTS} (SWORD_TEX_ONLY)")
log(f"готово: {count} текстур → {OUT}")
