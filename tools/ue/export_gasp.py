"""
Экспорт анимаций из Game Animation Sample (Unreal Editor) в raw-формат SWORD
(manifest.json + clips/*.bin), который печёт пекарня (обработчик anim.pack).

Запуск в проекте GASP: включить плагин Python Editor Script Plugin, затем
Tools → Execute Python Script → этот файл. Или в Output Log (Python):
    import sys; sys.argv = ["export_gasp.py", "--dry-run"]; exec(open(r"<путь>/export_gasp.py").read())
Или commandlet UnrealEditor-Cmd … -run=pythonscript с конфигом в SWORD_EXPORT_CONFIG — команды в tools/ue/README.md.

--dry-run — только список подходящих клипов, кости и кривые первого клипа: запускать первым,
API Python меняется между версиями UE.

Формат .bin: кадры × кости × 7 float32 (pos xyz в см, quat xyzw; component space,
root motion — в кости root), затем кадры × кривые float32.

Лицензия: контент Epic — ответственность пользователя; результат не коммитить (см. .gitignore).
"""

import array
import json
import os
import re
import sys

import unreal

HERE = os.path.dirname(os.path.abspath(__file__)) if "__file__" in globals() else os.getcwd()


def load_config():
    # В commandlet (-run=pythonscript) __file__ бывает не задан — путь можно передать переменной окружения.
    path = os.environ.get("SWORD_EXPORT_CONFIG") or os.path.join(HERE, "export_config.json")
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def log(msg):
    unreal.log("[sword-export] " + msg)


def warn(msg):
    unreal.log_warning("[sword-export] " + msg)


def find_sequences(cfg):
    include = [re.compile(p) for p in cfg["include"]]
    exclude = [re.compile(p) for p in cfg["exclude"]]
    paths = unreal.EditorAssetLibrary.list_assets(cfg["root"], recursive=True, include_folder=False)
    result = []
    for path in sorted(paths):
        if include and not any(r.search(path) for r in include):
            continue
        if any(r.search(path) for r in exclude):
            continue
        data = unreal.EditorAssetLibrary.find_asset_data(path)
        cls = str(data.asset_class_path.asset_name) if hasattr(data, "asset_class_path") else str(data.asset_class)
        if cls != "AnimSequence":
            continue
        result.append(path)
    if cfg.get("maxClips", 0) > 0:
        result = result[: cfg["maxClips"]]
    return result


_BONES = {}


def resolve_bones(seq, cfg):
    """bones: ["*"] — все кости скелета (для скиннинга), порядок и родители — из позы; иначе список из конфига."""
    if cfg["bones"] != ["*"]:
        return cfg["bones"]
    if "names" not in _BONES:
        pose = unreal.AnimPoseExtensions.get_anim_pose_at_time(seq, 0.0, eval_options())
        names = [str(b) for b in unreal.AnimPoseExtensions.get_bone_names(pose)]
        # В UE 5.8 у AnimPoseExtensions нет get_parent_bone_name (раньше ошибка глоталась, и все кости
        # выходили корнями). Иерархия — из референсного скелета меша через SkeletonModifier.
        mesh_path = cfg.get("parentsMesh")
        if not mesh_path:
            raise RuntimeError('для "bones": ["*"] нужен "parentsMesh" — скелетный меш с тем же скелетом')
        mesh = unreal.EditorAssetLibrary.load_asset(mesh_path)
        if mesh is None:
            raise RuntimeError(f"не загрузился parentsMesh {mesh_path}")
        modifier = unreal.SkeletonModifier()
        modifier.set_skeletal_mesh(mesh)
        known = set(str(b) for b in modifier.get_all_bone_names())
        parents = []
        orphans = []
        for n in names:
            if n not in known:
                # виртуальные кости (VB …) есть только в скелете анимации — корнем
                parents.append(-1)
                orphans.append(n)
                continue
            p = str(modifier.get_parent_name(n))
            parents.append(names.index(p) if p in names else -1)
        roots = [n for n, p in zip(names, parents) if p < 0]
        log(f"иерархия: {len(names)} костей, корней {len(roots)}: {roots}; не в меше: {orphans}")
        if len(roots) > 1 + len(orphans) + 2:
            raise RuntimeError(f"слишком много корней ({len(roots)}) — иерархия не прочиталась")
        _BONES["names"], _BONES["parents"] = names, parents
    return _BONES["names"]


def eval_options():
    opts = unreal.AnimPoseEvaluationOptions()
    # root motion должна остаться в кости root; имена свойств отличаются по версиям
    for name, value in (("extract_root_motion", False), ("should_retarget", False)):
        try:
            opts.set_editor_property(name, value)
        except Exception:
            pass
    return opts


def curve_sampler(seq, name):
    """Кривая float по времени: линейная интерполяция ключей (API ключей есть во всех UE5)."""
    try:
        times, values = unreal.AnimationLibrary.get_float_keys(seq, name)
    except Exception as e:
        warn(f"кривая {name}: {e}")
        return lambda t: 0.0
    times, values = list(times), list(values)
    if not times:
        return lambda t: 0.0

    def sample(t):
        if t <= times[0]:
            return values[0]
        for i in range(1, len(times)):
            if t <= times[i]:
                a = (t - times[i - 1]) / max(times[i] - times[i - 1], 1e-6)
                return values[i - 1] + (values[i] - values[i - 1]) * a
        return values[-1]

    return sample


def export_clip(seq, path, cfg, out_dir, dry_run):
    name = path.split("/")[-1].split(".")[0]
    length = unreal.AnimationLibrary.get_sequence_length(seq)
    fps = float(cfg["sampleFps"])
    frames = int(round(length * fps)) + 1
    loop = re.search(cfg["loopRegex"], name) is not None
    bones = resolve_bones(seq, cfg)

    curve_names = []
    try:
        curve_names = [str(c) for c in unreal.AnimationLibrary.get_animation_curve_names(seq, unreal.RawCurveTrackTypes.RCT_FLOAT)]
    except Exception as e:
        warn(f"{name}: имена кривых недоступны ({e})")
    wanted = cfg["curves"] if cfg["curves"] else []
    curves = [c for c in wanted if c in curve_names]

    info = {
        "name": name, "path": path, "file": name + ".bin", "fps": fps, "frames": frames, "loop": loop,
        "curves": curves, "syncMarkers": [], "notifies": [],
    }
    try:
        for m in unreal.AnimationLibrary.get_anim_sync_markers(seq):
            info["syncMarkers"].append({"name": str(m.get_editor_property("marker_name")), "time": float(m.get_editor_property("time"))})
    except Exception as e:
        warn(f"{name}: sync markers недоступны ({e})")
    try:
        for n in unreal.AnimationLibrary.get_animation_notify_events(seq):
            notify = n.get_editor_property("notify")
            label = notify.get_class().get_name() if notify else str(n.get_editor_property("notify_name"))
            info["notifies"].append({"name": label, "time": float(n.get_editor_property("trigger_time_offset")),
                                     "duration": float(n.get_editor_property("duration"))})
    except Exception as e:
        warn(f"{name}: notifies недоступны ({e})")

    if dry_run:
        pose = unreal.AnimPoseExtensions.get_anim_pose_at_time(seq, 0.0, eval_options())
        present = [str(b) for b in unreal.AnimPoseExtensions.get_bone_names(pose)]
        missing = [b for b in bones if b not in present]
        log(f"{name}: {length:.2f} с, {frames} кадров, loop={loop}, костей в позе {len(present)}, нет нужных: {missing or '—'}")
        log(f"  кривые: {curve_names}")
        log(f"  sync markers: {[m['name'] for m in info['syncMarkers']]}")
        return info

    opts = eval_options()
    samplers = [curve_sampler(seq, c) for c in curves]
    data = array.array("f")
    curve_data = array.array("f")
    for f in range(frames):
        t = min(f / fps, length)
        pose = unreal.AnimPoseExtensions.get_anim_pose_at_time(seq, t, opts)
        for b in bones:
            x = unreal.AnimPoseExtensions.get_bone_pose(pose, b, unreal.AnimPoseSpaces.WORLD)
            p, q = x.translation, x.rotation
            data.extend((p.x, p.y, p.z, q.x, q.y, q.z, q.w))
        for s in samplers:
            curve_data.append(s(t))
    with open(os.path.join(out_dir, "clips", info["file"]), "wb") as f:
        data.tofile(f)
        curve_data.tofile(f)
    return info


def main():
    cfg = load_config()
    dry_run = "--dry-run" in sys.argv or os.environ.get("SWORD_EXPORT_DRYRUN") == "1"
    out_dir = cfg["out"]
    paths = find_sequences(cfg)
    log(f"подходящих AnimSequence: {len(paths)} в {cfg['root']}")
    if not paths:
        warn("ничего не найдено — проверьте root/include в export_config.json")
        return
    if dry_run:
        for p in paths:
            log("  " + p)
    else:
        os.makedirs(os.path.join(out_dir, "clips"), exist_ok=True)

    clips = []
    with unreal.ScopedSlowTask(len(paths), "SWORD: экспорт анимаций") as task:
        try:
            task.make_dialog(True)
        except Exception:
            pass  # commandlet без окна
        for i, path in enumerate(paths):
            if task.should_cancel():
                break
            task.enter_progress_frame(1, path)
            seq = unreal.EditorAssetLibrary.load_asset(path)
            if not isinstance(seq, unreal.AnimSequence):
                continue
            clips.append(export_clip(seq, path, cfg, out_dir, dry_run))
            if dry_run:
                break  # подробности — по первому клипу

    if dry_run:
        return
    manifest = {
        "format": "sword-rawanim", "version": 1, "units": "cm", "axes": "ue",
        "rootForward": cfg["rootForward"], "bones": _BONES.get("names", cfg["bones"]), "clips": clips,
    }
    if "parents" in _BONES:
        manifest["parents"] = _BONES["parents"]
    with open(os.path.join(out_dir, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
    log(f"готово: {len(clips)} клипов → {out_dir}")


main()
