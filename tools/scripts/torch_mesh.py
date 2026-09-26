"""
Меш факела — исходник weapon.static пекарни (Bake/weapons/torch.bake.json): палка и шишка по размерам факела LIVE-2
(палка Ø5 × 80 см, шишка — эллипсоид 5×5×4 см с центром на конце палки). Пишет glTF 2.0 binary без сторонних пакетов.

Оси и единицы — как у выгрузок UE (tools/ue/export_weapons.py): glTF, Y вверх, сантиметры (unit пекарни 1). Палка — по +Y
от нижнего конца (y = 0, навершие для пекарни) до 80 см, шишка — до 82 см. Палка сужается от Ø5,2 у нижнего конца к Ø4,8
у шишки (в среднем Ø5): пекарня ищет рукоять со стороны самого широкого сечения (WeaponBake), поэтому самое широкое
сечение — нижний конец, а не шишка. Материалы — «wood» (палка) и «charred» (шишка): по ним пекарня делит меш на секции
(weapon.static, sections), а игра красит части.

Запуск: python tools/scripts/torch_mesh.py [--out ПУТЬ] (по умолчанию ${src}/_Export/weapons/torch.glb, src — Bake/bake.user.json).
"""
import argparse
import json
import math
import os
import struct

# Корень выгрузок — ${src}/_Export пекарни: roots.src из Bake/bake.user.json (нет файла — прежний путь этой машины).
FALLBACK_SRC = "D:/Temp/GamedevAssets"


def default_out():
    user = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Bake", "bake.user.json")
    try:
        with open(user, encoding="utf-8") as f:
            src = json.load(f)["roots"]["src"]
    except (OSError, KeyError, ValueError):
        src = FALLBACK_SRC
    return os.path.join(src, "_Export", "weapons", "torch.glb").replace("\\", "/")

LENGTH = 80.0        # палка, см
R_BOTTOM = 2.6       # радиус палки у нижнего конца
R_TOP = 2.4          # радиус палки у шишки
KNOB_R = 2.5         # полуось шишки поперёк палки (5 см)
KNOB_H = 2.0         # полуось шишки вдоль палки (4 см)
SEGMENTS = 24        # вокруг оси
KNOB_RINGS = 12      # от полюса до полюса шишки

MATERIALS = [
    ("wood", [0.36, 0.22, 0.11, 1.0]),
    ("charred", [0.04, 0.035, 0.03, 1.0]),
]


class Part:
    def __init__(self, material):
        self.material = material
        self.pos, self.nrm, self.uv, self.idx = [], [], [], []

    def vertex(self, p, n, uv):
        length = math.sqrt(sum(c * c for c in n))
        self.pos.append(p)
        self.nrm.append(tuple(c / length for c in n))
        self.uv.append(uv)
        return len(self.pos) - 1


def ring(part, y, radius, normal_of, v):
    """Кольцо вершин (SEGMENTS + 1: шов со своими UV); normal_of(cos, sin) — нормаль."""
    out = []
    for i in range(SEGMENTS + 1):
        a = 2 * math.pi * i / SEGMENTS
        c, s = math.cos(a), math.sin(a)
        out.append(part.vertex((radius * c, y, radius * s), normal_of(c, s), (i / SEGMENTS, v)))
    return out


def band(part, lower, upper):
    """Полоса между кольцами: лицевая сторона — наружу (обход против часовой, glTF)."""
    for i in range(SEGMENTS):
        part.idx += [lower[i], upper[i], lower[i + 1], lower[i + 1], upper[i], upper[i + 1]]


def stick():
    part = Part(0)
    slope = (R_BOTTOM - R_TOP) / LENGTH      # конус сужается кверху: нормаль чуть вверх
    side = lambda c, s: (c, slope, s)
    bottom = ring(part, 0.0, R_BOTTOM, side, 0.0)
    top = ring(part, LENGTH, R_TOP, side, LENGTH / (LENGTH + KNOB_H))
    band(part, bottom, top)
    # нижний торец; верхний закрыт шишкой
    down = lambda c, s: (0.0, -1.0, 0.0)
    cap = ring(part, 0.0, R_BOTTOM, down, 0.0)
    centre = part.vertex((0.0, 0.0, 0.0), (0.0, -1.0, 0.0), (0.5, 0.0))
    for i in range(SEGMENTS):
        part.idx += [centre, cap[i], cap[i + 1]]
    return part


def knob():
    part = Part(1)
    top = part.vertex((0.0, LENGTH + KNOB_H, 0.0), (0.0, 1.0, 0.0), (0.5, 1.0))
    bottom = part.vertex((0.0, LENGTH - KNOB_H, 0.0), (0.0, -1.0, 0.0), (0.5, 0.0))
    rings = []
    for j in range(1, KNOB_RINGS):
        phi = math.pi * j / KNOB_RINGS
        dy, rho = KNOB_H * math.cos(phi), KNOB_R * math.sin(phi)
        # нормаль эллипсоида — градиент (x/a², y/c², z/a²)
        normal = lambda c, s, rho=rho, dy=dy: (rho * c / KNOB_R ** 2, dy / KNOB_H ** 2, rho * s / KNOB_R ** 2)
        rings.append(ring(part, LENGTH + dy, rho, normal, 1.0 - j / KNOB_RINGS))
    for i in range(SEGMENTS):
        part.idx += [top, rings[0][i + 1], rings[0][i]]
    for upper, lower in zip(rings, rings[1:]):
        band(part, lower, upper)
    for i in range(SEGMENTS):
        part.idx += [bottom, rings[-1][i], rings[-1][i + 1]]
    return part


def glb(parts):
    """glTF 2.0 binary: один меш, примитив на часть со своим материалом."""
    blob = bytearray()
    views, accessors, primitives = [], [], []

    def add(data, target, count, kind, component, extra=None):
        while len(blob) % 4:
            blob.append(0)
        views.append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(data), "target": target})
        blob.extend(data)
        acc = {"bufferView": len(views) - 1, "componentType": component, "count": count, "type": kind}
        acc.update(extra or {})
        accessors.append(acc)
        return len(accessors) - 1

    for p in parts:
        flat = [c for v in p.pos for c in v]
        lo = [min(v[k] for v in p.pos) for k in range(3)]
        hi = [max(v[k] for v in p.pos) for k in range(3)]
        position = add(struct.pack(f"<{len(flat)}f", *flat), 34962, len(p.pos), "VEC3", 5126, {"min": lo, "max": hi})
        flat = [c for v in p.nrm for c in v]
        normal = add(struct.pack(f"<{len(flat)}f", *flat), 34962, len(p.nrm), "VEC3", 5126)
        flat = [c for v in p.uv for c in v]
        uv = add(struct.pack(f"<{len(flat)}f", *flat), 34962, len(p.uv), "VEC2", 5126)
        indices = add(struct.pack(f"<{len(p.idx)}H", *p.idx), 34963, len(p.idx), "SCALAR", 5123)
        primitives.append({"attributes": {"POSITION": position, "NORMAL": normal, "TEXCOORD_0": uv},
                           "indices": indices, "material": p.material, "mode": 4})
    while len(blob) % 4:
        blob.append(0)
    doc = {
        "asset": {"version": "2.0", "generator": "SWORD2 tools/scripts/torch_mesh.py"},
        "scene": 0, "scenes": [{"nodes": [0]}], "nodes": [{"mesh": 0, "name": "torch"}],
        "meshes": [{"name": "torch", "primitives": primitives}],
        "materials": [{"name": name, "pbrMetallicRoughness": {"baseColorFactor": colour, "metallicFactor": 0.0,
                                                               "roughnessFactor": 0.9}} for name, colour in MATERIALS],
        "buffers": [{"byteLength": len(blob)}], "bufferViews": views, "accessors": accessors,
    }
    text = json.dumps(doc, separators=(",", ":")).encode("utf-8")
    text += b" " * (-len(text) % 4)
    body = struct.pack("<II", len(text), 0x4E4F534A) + text + struct.pack("<II", len(blob), 0x004E4942) + bytes(blob)
    return struct.pack("<III", 0x46546C67, 2, 12 + len(body)) + body


def main():
    ap = argparse.ArgumentParser(description="Меш факела (палка и шишка LIVE-2) → .glb для weapon.static")
    ap.add_argument("--out", default=default_out(), help="куда записать .glb (по умолчанию ${src}/_Export/weapons/torch.glb)")
    out = ap.parse_args().out
    parts = [stick(), knob()]
    data = glb(parts)
    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)
    with open(out, "wb") as f:
        f.write(data)
    verts = sum(len(p.pos) for p in parts)
    tris = sum(len(p.idx) for p in parts) // 3
    print(f"факел: {out}, {verts} вершин, {tris} треугольников, длина {LENGTH + KNOB_H:.0f} см")


if __name__ == "__main__":
    main()
