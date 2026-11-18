# -*- coding: utf-8 -*-
"""
【taiki】間取りJSON（Unity の Tools > taiki > 間取りを書き出す で作成）を読み、
  ・歩ける場所をつないだグラフ（複数階・階段対応）
  ・最短経路 / ゴールまでの「歩ける道のり」
  ・階ごとの背景画像（床・壁/障害物・段差/階段）
を計算するモジュール。taiki_visualize_position.py から使う。
"""
import json
import math

import numpy as np
from scipy.sparse import coo_matrix
from scipy.sparse.csgraph import connected_components, dijkstra
from scipy.spatial import cKDTree

CAT_NONE, CAT_FLOOR, CAT_WALL, CAT_STAIRS = 0, 1, 2, 3


class taiki_Layout:
    def __init__(self, path, goal_override=None, scale=1.0):
        """ scale: Unityの1単位が何mか（実験場は約0.29）。距離はすべて m に直して扱う """
        with open(path, encoding="utf-8") as f:
            d = json.load(f)
        self.scene = d.get("scene", "")
        self.cell = float(d["cell"]) * scale
        self.x0, self.z0 = (float(v) * scale for v in d["origin"])
        self.nx, self.nz = int(d["nx"]), int(d["nz"])
        self.step = float(d.get("step_height", 0.45)) * scale
        self.markers = []
        for m in d.get("markers", []):
            m = dict(m)
            for k in ("x", "y", "z", "sx", "sz"):
                if k in m:
                    m[k] = float(m[k]) * scale
            self.markers.append(m)
        s = np.asarray(d["surfaces"], dtype=float).reshape(-1, 4)
        self.surf_i = s[:, 0].astype(int)
        self.surf_j = s[:, 1].astype(int)
        self.surf_h = s[:, 2] * scale
        self.surf_w = s[:, 3].astype(bool)
        self._taiki_build_graph()
        self.goal_nodes = self._taiki_find_goal_nodes(goal_override)
        self._taiki_reachable()
        self.floors = [dict(f, y_min=f["y_min"] * scale, y_max=f["y_max"] * scale) for f in d.get("floors", [])] \
            or self._taiki_auto_floors()
        for k, fl in enumerate(self.floors):
            fl["level"] = fl.get("level", self._taiki_floor_level(fl))
        self.node_floor = self.taiki_floor_of_height(self.node_h)
        self.dist_to_goal = None
        self.pred = None
        if len(self.goal_nodes):
            dist, pred, src = dijkstra(self.graph, directed=False, indices=self.goal_nodes,
                                       return_predecessors=True, min_only=True)
            self.dist_to_goal, self.pred = dist, pred

    # ------------------------------------------------------------ グラフ
    def _taiki_build_graph(self):
        w = self.surf_w
        self.node_i, self.node_j, self.node_h = self.surf_i[w], self.surf_j[w], self.surf_h[w]
        self.node_x = self.x0 + (self.node_i + 0.5) * self.cell
        self.node_z = self.z0 + (self.node_j + 0.5) * self.cell
        n = len(self.node_h)
        # 列(i,j) → その列にあるノード番号のリスト
        self.col = {}
        for idx, (i, j) in enumerate(zip(self.node_i, self.node_j)):
            self.col.setdefault((i, j), []).append(idx)
        rows, cols, wts = [], [], []
        # 上下左右（斜めは角をすり抜けないよう、両隣の通路があるときだけ）
        offs = [(1, 0), (0, 1), (1, 1), (1, -1)]
        for idx in range(n):
            i, j, h = self.node_i[idx], self.node_j[idx], self.node_h[idx]
            for di, dj in offs:
                for nb in self.col.get((i + di, j + dj), ()):
                    dh = abs(self.node_h[nb] - h)
                    if dh > self.step:
                        continue
                    if di != 0 and dj != 0:
                        if not (self._taiki_has_near(i + di, j, h) and self._taiki_has_near(i, j + dj, h)):
                            continue
                    dxy = self.cell * math.hypot(di, dj)
                    rows.append(idx); cols.append(nb); wts.append(math.hypot(dxy, dh))
        self.graph = coo_matrix((wts, (rows, cols)), shape=(n, n)).tocsr()
        self.kdt = cKDTree(np.c_[self.node_x, self.node_z, self.node_h * 2.0])

    def _taiki_has_near(self, i, j, h):
        return any(abs(self.node_h[k] - h) <= self.step for k in self.col.get((i, j), ()))

    def _taiki_reachable(self):
        ncomp, labels = connected_components(self.graph, directed=False)
        if len(self.goal_nodes):
            keep = set(labels[self.goal_nodes])
        else:
            keep = {np.bincount(labels).argmax()}
        self.reachable = np.isin(labels, list(keep))

    def _taiki_find_goal_nodes(self, goal_override):
        cands = []
        goals = [m for m in self.markers if m.get("kind") == "goal"]
        if goal_override is not None:
            goals = [dict(x=goal_override[0], y=goal_override[1], z=goal_override[2], sx=1.0, sz=1.0)]
        for g in goals:
            hx, hz = max(g.get("sx", 0), 0.5) / 2 + 0.3, max(g.get("sz", 0), 0.5) / 2 + 0.3
            m = ((np.abs(self.node_x - g["x"]) <= hx) & (np.abs(self.node_z - g["z"]) <= hz) &
                 (np.abs(self.node_h - g["y"]) <= 1.5))
            idx = np.nonzero(m)[0]
            if len(idx) == 0:
                _, k = self.kdt.query([g["x"], g["z"], g["y"] * 2.0])
                idx = [k]
            cands.extend(idx)
        return np.unique(np.asarray(cands, dtype=int))

    # ------------------------------------------------------------ 階
    def _taiki_auto_floors(self):
        h = self.node_h[self.reachable] if self.reachable.any() else self.node_h
        if len(h) == 0:
            return [dict(name="1F", y_min=-1e9, y_max=1e9)]
        bins = np.arange(h.min() - 0.25, h.max() + 0.5, 0.25)
        cnt, edges = np.histogram(h, bins=bins)
        levels = []
        for k in np.argsort(-cnt):
            if cnt[k] < 0.08 * cnt.max():
                break
            c = (edges[k] + edges[k + 1]) / 2
            if all(abs(c - l) > 2.0 for l in levels):
                levels.append(c)
        levels.sort()
        floors = []
        for k, l in enumerate(levels):
            lo = -1e9 if k == 0 else l - 0.6
            hi = 1e9 if k == len(levels) - 1 else levels[k + 1] - 0.6
            floors.append(dict(name=f"{k + 1}F", y_min=lo, y_max=hi, level=l))
        return floors

    def _taiki_floor_level(self, fl):
        h = self.node_h[self.reachable & (self.node_h >= fl["y_min"]) & (self.node_h < fl["y_max"])]
        if len(h) == 0:
            return (fl["y_min"] + fl["y_max"]) / 2
        vals, cnt = np.unique(np.round(h * 4) / 4, return_counts=True)
        return float(vals[cnt.argmax()])

    def taiki_floor_of_height(self, h):
        h = np.asarray(h, dtype=float)
        out = np.zeros(h.shape, dtype=int)
        for k, fl in enumerate(self.floors):
            out[(h >= fl["y_min"]) & (h < fl["y_max"])] = k
        return out

    # ------------------------------------------------------------ 背景
    def taiki_floor_raster(self, k):
        """ 階 k の背景（nz x nx の分類画像）"""
        fl = self.floors[k]
        lvl = fl["level"]
        img = np.full((self.nz, self.nx), CAT_NONE, dtype=np.int8)
        band = (self.surf_h >= fl["y_min"]) & (self.surf_h < fl["y_max"])
        img[self.surf_j[band], self.surf_i[band]] = CAT_WALL     # 何か物がある
        r = self.reachable & (self.node_floor == k)
        stairs = r & (np.abs(self.node_h - lvl) > 0.3)
        flat = r & ~stairs
        img[self.node_j[stairs], self.node_i[stairs]] = CAT_STAIRS
        img[self.node_j[flat], self.node_i[flat]] = CAT_FLOOR
        return img

    def taiki_extent(self):
        return (self.x0, self.x0 + self.nx * self.cell, self.z0, self.z0 + self.nz * self.cell)

    def taiki_floor_bbox(self, k, pad=1.5):
        img = self.taiki_floor_raster(k)
        js, is_ = np.nonzero((img == CAT_FLOOR) | (img == CAT_STAIRS))
        if len(is_) == 0:
            return self.taiki_extent()
        x0, x1 = self.x0 + is_.min() * self.cell, self.x0 + (is_.max() + 1) * self.cell
        z0, z1 = self.z0 + js.min() * self.cell, self.z0 + (js.max() + 1) * self.cell
        for m in self.taiki_markers_on_floor(k):   # 炎や障害物が床の外にはみ出していても入るように
            hx, hz = max(m.get("sx", 0), 0.6) / 2, max(m.get("sz", 0), 0.6) / 2
            x0, x1 = min(x0, m["x"] - hx), max(x1, m["x"] + hx)
            z0, z1 = min(z0, m["z"] - hz), max(z1, m["z"] + hz)
        return (x0 - pad, x1 + pad, z0 - pad, z1 + pad)

    def taiki_markers_on_floor(self, k):
        fl = self.floors[k]
        return [m for m in self.markers if fl["y_min"] - 0.5 <= m.get("y", 0) < fl["y_max"]]

    # ------------------------------------------------------------ ログとの対応
    def taiki_snap(self, x, y, z, height_offset=1.2):
        """ ログの座標 → 一番近い歩ける地点（ノード番号） """
        q = np.c_[x, z, (np.asarray(y) - height_offset) * 2.0]
        _, idx = self.kdt.query(q)
        return idx

    def taiki_shortest_path(self, start_node):
        """ start_node から最寄りのゴールまでの最短経路（ノード番号の列）と長さ """
        if self.pred is None or not np.isfinite(self.dist_to_goal[start_node]):
            return None, float("nan")
        path = [start_node]
        while self.pred[path[-1]] >= 0:
            path.append(self.pred[path[-1]])
            if len(path) > 200000:
                break
        return np.asarray(path), float(self.dist_to_goal[start_node])
