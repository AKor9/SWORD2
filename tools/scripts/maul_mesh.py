"""
Меш кувалды (боевого молота на двух руках) — исходник weapon.static пекарни
(Bake/recipes/src/_Export/weapons/maul.glb.bake.json): деревянная рукоять Ø4 и стальная головка-брусок 12×12×20 см
на дальнем конце, длина 110 см. Пробное оружие. Пишет glTF 2.0 binary без сторонних пакетов.

Оси и единицы — как у факела (tools/scripts/torch_mesh.py) и выгрузок UE: glTF, Y вверх, сантиметры (unit пекарни 1).
Рукоять — по +Y от нижнего конца (y = 0, навершие для пекарни) до середины головки; головка — от 98 до 110 см по Y,
длинная сторона (20 см) — поперёк рукояти по X. Рукоять сужается от Ø4,2 у нижнего конца к Ø3,8 у головки (в среднем Ø4).
Материалы — «wood» (рукоять) и «steel» (головка): по ним пекарня делит меш на секции (weapon.static, sections), а игра
красит части.

Метки хвата. Пекарня ищет рукоять со стороны самого широкого сечения (WeaponBake: «гарда — самое широкое сечение»),
а головка кувалды (радиус угла бруска ≈ 11,7 см) шире любой рукояти — без меток навершием стала бы головка. Поэтому
у нижнего конца рукояти — четыре вершины на радиусе 12,5 см, симметрично вокруг оси (центр сечения остаётся на оси),
не входящие ни в один треугольник: их не видно, но сечение навершия самое широкое. Кончик оружия игры — самая
дальняя от кисти вершина — остаётся углом головки. Цена: цилиндр зоны крови рукояти — радиуса меток.

Запуск: python tools/scripts/maul_mesh.py [--out ПУТЬ] (по умолчанию ${src}/_Export/weapons/maul.glb, src — Bake/bake.user.json).
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
    return os.path.join(src, "_Export", "weapons", "maul.glb").replace("\\", "/")

LENGTH = 110.0       # всё оружие, см
HEAD_ALONG = 12.0    # головка вдоль рукояти
HEAD_ACROSS = 20.0   # головка поперёк рукояти (ударные торцы), по X
HEAD_DEPTH = 12.0    # головка поперёк рукояти, по Z
SHAFT_TOP = LENGTH - HEAD_ALONG / 2   # верх рукояти — внутри головки
R_BOTTOM = 2.1       # радиус рукояти у нижнего конца
R_TOP = 1.9          # радиус рукояти у головки
MARK_R = 12.5        # радиус меток хвата: больше угла головки √(10² + 6²) ≈ 11,7
SEGMENTS = 24        # вокруг оси

MATERIALS = [
    ("wood", [0.36, 0.22, 0.11, 1.0]),
    ("steel", [0.8, 0.82, 0.86, 1.0]),
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


def shaft():
    part = Part(0)
    slope = (R_BOTTOM - R_TOP) / SHAFT_TOP   # конус сужается кверху: нормаль чуть вверх
    side = lambda c, s: (c, slope, s)
    bottom = ring(part, 0.0, R_BOTTOM, side, 0.0)
    top = ring(part, SHAFT_TOP, R_TOP, side, 1.0)
    band(part, bottom, top)
    # нижний торец; верхний конец спрятан в головке
    down = lambda c, s: (0.0, -1.0, 0.0)
    cap = ring(part, 0.0, R_BOTTOM, down, 0.0)
    centre = part.vertex((0.0, 0.0, 0.0), (0.0, -1.0, 0.0), (0.5, 0.0))
    for i in range(SEGMENTS):
        part.idx += [centre, cap[i], cap[i + 1]]
    # метки хвата: вершины без треугольников (см. описание модуля)
    for k in range(4):
        a = math.pi / 2 * k
        part.vertex((MARK_R * math.cos(a), 0.0, MARK_R * math.sin(a)), (0.0, -1.0, 0.0), (0.5, 0.0))
    return part


def head():
    """Брусок: грань — 4 вершины со своей нормалью (острые рёбра), обход против часовой снаружи."""
    part = Part(1)
    hx, hz = HEAD_ACROSS / 2, HEAD_DEPTH / 2
    y0, y1 = LENGTH - HEAD_ALONG, LENGTH
    cy = (y0 + y1) / 2
    hy = HEAD_ALONG / 2
    centre = (0.0, cy, 0.0)
    half = (hx, hy, hz)
    for axis in range(3):
        for sign in (1.0, -1.0):
            n = [0.0, 0.0, 0.0]
            n[axis] = sign
            # u, v — оси грани так, что u × v = n
            u_axis, v_axis = (axis + 1) % 3, (axis + 2) % 3
            if sign < 0:
                u_axis, v_axis = v_axis, u_axis
            corners = []
            for du, dv in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
                p = list(centre)
                p[axis] += sign * half[axis]
                p[u_axis] += du * half[u_axis]
                p[v_axis] += dv * half[v_axis]
                corners.append(part.vertex(tuple(p), tuple(n), ((du + 1) / 2, (dv + 1) / 2)))
            a, b, c, d = corners
            part.idx += [a, b, c, a, c, d]
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
        "asset": {"version": "2.0", "generator": "SWORD2 tools/scripts/maul_mesh.py"},
        "scene": 0, "scenes": [{"nodes": [0]}], "nodes": [{"mesh": 0, "name": "maul"}],
        "meshes": [{"name": "maul", "primitives": primitives}],
        "materials": [{"name": name, "pbrMetallicRoughness": {"baseColorFactor": colour,
                                                               "metallicFactor": 1.0 if name == "steel" else 0.0,
                                                               "roughnessFactor": 0.5 if name == "steel" else 0.9}}
                      for name, colour in MATERIALS],
        "buffers": [{"byteLength": len(blob)}], "bufferViews": views, "accessors": accessors,
    }
    text = json.dumps(doc, separators=(",", ":")).encode("utf-8")
    text += b" " * (-len(text) % 4)
    body = struct.pack("<II", len(text), 0x4E4F534A) + text + struct.pack("<II", len(blob), 0x004E4942) + bytes(blob)
    return struct.pack("<III", 0x46546C67, 2, 12 + len(body)) + body


def main():
    ap = argparse.ArgumentParser(description="Меш кувалды (рукоять и стальная головка) → .glb для weapon.static")
    ap.add_argument("--out", default=default_out(), help="куда записать .glb (по умолчанию ${src}/_Export/weapons/maul.glb)")
    out = ap.parse_args().out
    parts = [shaft(), head()]
    data = glb(parts)
    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)
    with open(out, "wb") as f:
        f.write(data)
    verts = sum(len(p.pos) for p in parts)
    tris = sum(len(p.idx) for p in parts) // 3
    print(f"кувалда: {out}, {verts} вершин, {tris} треугольников, длина {LENGTH:.0f} см")


if __name__ == "__main__":
    main()
