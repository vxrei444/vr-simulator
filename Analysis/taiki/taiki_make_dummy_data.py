# -*- coding: utf-8 -*-
"""
【taiki】図の動作確認用ダミーデータを作る（※実験データではない）

  sample_DUMMY/layout_dummy_2floors.json … 2階建ての間取り（taiki_LayoutExporter と同じ形式）
      2F の部屋からスタート → 西の階段で 1F へ → 東の出口がゴール。
      2F 東側は炎で通れない。1F 廊下に障害物、1F 北に行き止まり。
  sample_DUMMY/logs/position_{条件}_{日時}.csv … 条件A〜D × 協力者5人 のダミー軌跡
"""
import json
import os
import sys

import numpy as np
from scipy.sparse.csgraph import dijkstra

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from taiki_layout import taiki_Layout  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "sample_DUMMY")
os.makedirs(os.path.join(OUT, "logs"), exist_ok=True)
rng = np.random.default_rng(7)

CELL, X0, Z0, X1, Z1 = 0.25, -2.0, -2.0, 43.0, 14.0
NX, NZ = int((X1 - X0) / CELL), int((Z1 - Z0) / CELL)


def taiki_inside(x, z, r):
    return r[0] <= x < r[1] and r[2] <= z < r[3]


# 床の領域 (x0, x1, z0, z1)
F1 = [(0, 41, 0, 2.5),          # 1F 東西の廊下（東端が出口）
      (18.75, 21.25, 2.5, 12),  # 1F 北へ延びる行き止まり
      (0, 2, 2.5, 5.5)]         # 1F 階段の下り口
F2 = [(10, 36, 3, 5.5),         # 2F 東西の廊下
      (14, 22, 5.5, 12)]        # 2F の部屋（スタート）
STAIRS = (2, 10, 3, 5)          # 西の階段：x=2 で 1F(0m) → x=10 で 2F(4m)
OBST_1F = (27.5, 29, 0, 1.5)    # 1F 廊下の障害物（瓦礫）
FIRE_2F = (28, 31, 3, 5.5)      # 2F 東側の炎（通れない）


def taiki_make_layout():
    surf = []
    walk = {0: np.zeros((NX, NZ), bool), 1: np.zeros((NX, NZ), bool)}
    for i in range(NX):
        for j in range(NZ):
            x, z = X0 + (i + 0.5) * CELL, Z0 + (j + 0.5) * CELL
            if any(taiki_inside(x, z, r) for r in F2):
                ok = not taiki_inside(x, z, FIRE_2F)
                surf.append([i, j, 4.0, int(ok)]); walk[1][i, j] = ok
            if taiki_inside(x, z, STAIRS):
                surf.append([i, j, round((x - 2) / 8 * 4, 3), 1])
            if any(taiki_inside(x, z, r) for r in F1):
                ok = not taiki_inside(x, z, OBST_1F)
                surf.append([i, j, 0.0, int(ok)]); walk[0][i, j] = ok
                if not ok:
                    surf.append([i, j, 1.2, 0])   # 障害物の上面
    # 床のまわりに壁（各階の床に隣接する何もない所）
    for f, h in ((0, 2.9), (1, 6.9)):
        w = walk[f]
        grown = w.copy()
        grown[1:] |= w[:-1]; grown[:-1] |= w[1:]; grown[:, 1:] |= w[:, :-1]; grown[:, :-1] |= w[:, 1:]
        for i, j in zip(*np.nonzero(grown & ~w)):
            x, z = X0 + (i + 0.5) * CELL, Z0 + (j + 0.5) * CELL
            if taiki_inside(x, z, STAIRS) or (f == 0 and x >= 41):
                continue
            surf.append([int(i), int(j), h, 0])
    markers = [
        dict(kind="fire", label="火元", x=29.5, y=4.0, z=4.25, sx=3.0, sz=2.5),
        dict(kind="obstacle", label="瓦礫", x=28.25, y=0.0, z=0.75, sx=1.5, sz=1.5),
        dict(kind="start", label="StartZone", x=18, y=4.0, z=9, sx=2, sz=2),
        dict(kind="goal", label="GoalZone_Exit", x=40.5, y=0.0, z=1.25, sx=1, sz=2.5),
    ]
    d = dict(version=1, scene="DUMMY_2floors", cell=CELL, origin=[X0, Z0], nx=NX, nz=NZ, agent_height=1.6,
             step_height=0.45, floors=[], markers=markers, surfaces=surf)
    path = os.path.join(OUT, "layout_dummy_2floors.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(d, f, ensure_ascii=False)
    return path


def taiki_route(lay, a, b):
    _, pred = dijkstra(lay.graph, directed=False, indices=a, return_predecessors=True)
    p = [b]
    while p[-1] != a and pred[p[-1]] >= 0:
        p.append(pred[p[-1]])
    return p[::-1]


def taiki_walk(lay, waypoints):
    """ 経由点を最短経路でつないで歩かせ、0.5秒ごとの頭の位置を返す """
    nodes = [lay.taiki_snap([x], [y + 1.6], [z])[0] for x, y, z in waypoints]
    pts = []
    for a, b in zip(nodes[:-1], nodes[1:]):
        r = taiki_route(lay, a, b)
        pts.append(np.c_[lay.node_x[r], lay.node_h[r], lay.node_z[r]])
        pts.append(None)   # 経由点で立ち止まる印
    t, out, dt = 0.0, [], 0.5
    for seg in pts:
        if seg is None:
            for _ in range(int(rng.uniform(3, 9) / dt)):
                p = out[-1][1:].copy(); p[0] += rng.normal(0, 0.05); p[2] += rng.normal(0, 0.05)
                t += dt; out.append([t, *p])
            continue
        d = np.r_[0, np.cumsum(np.linalg.norm(np.diff(seg, axis=0), axis=1))]
        s, v = 0.0, rng.uniform(1.1, 1.5)
        if not out:
            out.append([0.0, *seg[0]])
        while s < d[-1]:
            s = min(s + v * dt * rng.uniform(0.85, 1.1), d[-1])
            p = np.array([np.interp(s, d, seg[:, k]) for k in range(3)])
            p[0] += rng.normal(0, 0.06); p[2] += rng.normal(0, 0.06)
            t += dt; out.append([t, *p])
    out = np.array(out)
    out[:, 2] += 1.6   # 頭の高さ
    return out


START = (18, 4.0, 9)
GOAL = (40.6, 0.0, 1.25)
NEAR_FIRE = (26.5, 4.0, 4.25)
DEAD_END = (20, 0.0, 11.3)
ROUTES = {
    "direct": [START, GOAL],
    "fire": [START, NEAR_FIRE, GOAL],
    "dead": [START, DEAD_END, GOAL],
    "both": [START, NEAR_FIRE, DEAD_END, GOAL],
}
PLAN = {   # 条件ごとの迷い方（ダミー）
    "A": ["both", "fire", "dead", "both", "fire"],
    "B": ["direct", "dead", "direct", "fire", "direct"],
    "C": ["direct", "direct", "fire", "direct", "direct"],
    "D": ["fire", "direct", "direct", "dead", "direct"],
}

if __name__ == "__main__":
    lp = taiki_make_layout()
    lay = taiki_Layout(lp)
    for f in os.listdir(os.path.join(OUT, "logs")):
        os.remove(os.path.join(OUT, "logs", f))
    for ci, (cond, routes) in enumerate(PLAN.items()):
        for pi, r in enumerate(routes):
            sid = f"20261118_{15 + ci:02d}{pi * 7:02d}00"
            tr = taiki_walk(lay, ROUTES[r])
            with open(os.path.join(OUT, "logs", f"position_{cond}_{sid}.csv"), "w", encoding="utf-8", newline="\n") as f:
                f.write("session_id,participant,condition,elapsed_sec,x,y,z,yaw_deg\n")
                for t, x, y, z in tr:
                    f.write(f"{sid},P{pi + 1:02d},{cond},{t:.3f},{x:.3f},{y:.3f},{z:.3f},0.0\n")
    print("ダミーデータ出力:", OUT)
