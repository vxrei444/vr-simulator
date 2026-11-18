# -*- coding: utf-8 -*-
"""
【taiki】避難ログCSV → 図・表（PNG）

入力
  ・ログCSV（フォルダ指定可）
      taiki_PositionLogger      position_{条件}_{日時}.csv   列: session_id,condition,elapsed_sec,x,y,z,yaw_deg
      EvacuationTrajectoryLogger {日時}_{参加者}_{条件}_track.csv 列: t,pos_x,pos_y,pos_z,...
  ・--layout 間取りJSON（Unity メニュー Tools > taiki > 間取りを書き出す で作成）
      → 階ごとの背景・炎/障害物・最短経路・「歩ける道のり」でのゴール距離が使えるようになる

出力（--out、既定 figures/）
  A. 移動経路（階ごと）
     A1_trajectory_{条件}_{セッション}.png … 1プレイの経路（色＝経過時間、点線＝最短経路、炎・障害物つき）
     A2_trajectory_all.png                 … 全プレイを条件別の色で重ね描き（階ごと）
  B. 通った場所のヒートマップ
     B1_heatmap_by_condition.png           … 条件×階ごとの滞在時間（同じ色の尺度、1人平均）
  C. 最短経路と実際の経路の差
     C1_route_compare_{条件}_{セッション}.png … 最短経路との重ね描き＋ズレの時間変化
     C2_route_excess_by_condition.png      … 条件別：余分に歩いた距離・迂回率・最短経路から外れていた時間
  D. ゴールまでの距離の時系列
     D1_distance_to_goal_timeseries.png    … 横軸 時間 / 縦軸 ゴールまでの道のり（条件別の平均±標準誤差）
  E. 表
     E1_summary_table.png / summary.csv    … プレイごとの指標と条件別平均

使い方
  python taiki_visualize_position.py <CSVかフォルダ> --layout layout_xxx.json --out figures
"""
import argparse
import glob
import math
import os
import re
import sys

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
from matplotlib import font_manager
from matplotlib.collections import LineCollection
from matplotlib.colors import LinearSegmentedColormap, ListedColormap
from matplotlib.lines import Line2D
from matplotlib.patches import Patch, Rectangle
from scipy.spatial import cKDTree

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from taiki_layout import taiki_Layout  # noqa: E402

# ------------------------------------------------------------------ 見た目
for _f in ["Yu Gothic", "Meiryo", "MS Gothic", "Hiragino Sans", "Noto Sans CJK JP", "IPAexGothic", "Noto Sans CJK TC"]:
    if any(_f == f.name for f in font_manager.fontManager.ttflist):
        plt.rcParams["font.family"] = _f
        break
plt.rcParams.update({
    "axes.unicode_minus": False, "savefig.dpi": 200, "font.size": 10,
    "axes.spines.top": False, "axes.spines.right": False,
    "axes.edgecolor": "#8a8984", "axes.labelcolor": "#52514e",
    "xtick.color": "#52514e", "ytick.color": "#52514e",
    "axes.titlesize": 12, "axes.titleweight": "bold",
})
TEXT, MUTED, GRID = "#0b0b0b", "#8a8984", "#e4e3df"
BG_CMAP = ListedColormap(["#ffffff", "#efeee9", "#6b6a66", "#cfe0f3"])  # なし/床/壁・障害物/階段・段差
COND_COLORS = ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#6250d6", "#e34948"]
TIME_CMAP = LinearSegmentedColormap.from_list("time", ["#cfe2f7", "#2a78d6", "#0b2f5c"])
HEAT_CMAP = LinearSegmentedColormap.from_list("heat", ["#fdf3ee", "#f4a582", "#eb6834", "#7a2a0c"])
FIRE_C, OBST_C, GOAL_C = "#e34948", "#3d3c39", "#008300"

STOP_SPEED = 0.2      # これ未満(m/s)を立ち止まりとみなす
OFF_ROUTE_M = 1.5     # 最短経路からこれ以上離れていたら「外れている」
TS_STEP = 0.5         # 時系列の再サンプリング間隔(s)


# ------------------------------------------------------------------ 読み込み
def taiki_load_csv(path, scale=1.0):
    df = pd.read_csv(path, encoding="utf-8-sig")
    if "elapsed_sec" in df.columns:
        sid = str(df["session_id"].iloc[0]) if "session_id" in df else os.path.basename(path)
        cond = str(df["condition"].iloc[0]) if "condition" in df else "?"
        part = str(df["participant"].iloc[0]) if "participant" in df else sid
    elif "t" in df.columns and "pos_x" in df.columns:
        df = df.rename(columns={"t": "elapsed_sec", "pos_x": "x", "pos_y": "y", "pos_z": "z"})
        m = re.match(r"(\d{8}_\d{6})_(.+?)_(.+)_track\.csv$", os.path.basename(path))
        sid = m.group(1) if m else os.path.basename(path)
        part = m.group(2) if m else "?"
        cond = m.group(3) if m else "?"
    else:
        raise ValueError(f"列名が想定外です: {list(df.columns)}")
    if "y" not in df.columns:
        df["y"] = 0.0
    out = df[["elapsed_sec", "x", "y", "z"]].dropna().sort_values("elapsed_sec").reset_index(drop=True)
    out[["x", "y", "z"]] *= scale   # Unityの単位 → m
    out.attrs.update(session=sid, condition=cond, participant=part, file=os.path.basename(path))
    return out


def taiki_collect(inputs):
    files = []
    for p in inputs:
        if os.path.isdir(p):
            files += sorted(glob.glob(os.path.join(p, "position_*.csv")))
            files += sorted(glob.glob(os.path.join(p, "*_track.csv")))
        else:
            files += sorted(glob.glob(p))
    return files


def taiki_tag(df):
    return re.sub(r"[^\w\-]", "_", f"{df.attrs['condition']}_{df.attrs['participant']}_{df.attrs['session']}")


# ------------------------------------------------------------------ 指標
def taiki_enrich(df, lay, height_offset):
    t = df["elapsed_sec"].to_numpy()
    step = np.hypot(np.diff(df["x"], prepend=df["x"].iloc[0]), np.diff(df["z"], prepend=df["z"].iloc[0]))
    dt = np.diff(t, prepend=t[0])
    df["step_m"] = step
    df["dt"] = dt
    df["speed"] = np.where(dt > 1e-6, step / np.maximum(dt, 1e-6), 0.0)
    df.attrs["sp_nodes"], df.attrs["sp_len"] = None, float("nan")
    df["floor"], df["dist_goal"], df["dev"] = 0, np.nan, np.nan
    if lay is None:
        return df
    node = lay.taiki_snap(df["x"], df["y"], df["z"], height_offset)
    df["floor"] = lay.node_floor[node]
    df["gx"], df["gz"], df["gy"] = lay.node_x[node], lay.node_z[node], lay.node_h[node]
    if lay.dist_to_goal is not None:
        df["dist_goal"] = lay.dist_to_goal[node]
        path, sp_len = lay.taiki_shortest_path(int(node[0]))
        df.attrs["sp_nodes"], df.attrs["sp_len"] = path, sp_len
        if path is not None:
            # 高さ方向を3倍に重み付け → 別の階の最短経路とは「近い」と判定しない
            tree = cKDTree(np.c_[lay.node_x[path], lay.node_z[path], lay.node_h[path] * 3.0])
            df["dev"], _ = tree.query(np.c_[df["gx"], df["gz"], df["gy"] * 3.0])
    return df


def taiki_metrics(df):
    t = df["elapsed_sec"].to_numpy()
    total = t[-1] - t[0]
    dt = df["dt"].to_numpy()
    stopped = df["speed"].to_numpy() < STOP_SPEED
    stopped[0] = False
    path = float(df["step_m"].sum())
    sp = df.attrs.get("sp_len", float("nan"))
    has_dev = df["dev"].notna().any()
    last = df["dist_goal"].iloc[-1]
    return {
        "condition": df.attrs["condition"], "participant": df.attrs["participant"], "session": df.attrs["session"],
        "脱出時間[s]": round(total, 1),
        "ゴール到達": ("○" if last < 2.0 else "×") if np.isfinite(last) else "-",
        "移動距離[m]": round(path, 1),
        "最短距離[m]": round(sp, 1) if np.isfinite(sp) else np.nan,
        "余分な距離[m]": round(path - sp, 1) if np.isfinite(sp) else np.nan,
        "迂回率": round(path / sp, 2) if np.isfinite(sp) and sp > 0.1 else np.nan,
        "最短経路から外れた時間[s]": round(float(dt[df["dev"].to_numpy() > OFF_ROUTE_M].sum()), 1) if has_dev else np.nan,
        "最大のズレ[m]": round(float(df["dev"].max()), 1) if has_dev else np.nan,
        "立ち止まり時間[s]": round(float(dt[stopped].sum()), 1),
        "平均速度[m/s]": round(path / total, 2) if total > 0 else 0,
    }


# ------------------------------------------------------------------ 地図の下地
def taiki_cond_colors(conds):
    return {c: COND_COLORS[i % len(COND_COLORS)] for i, c in enumerate(conds)}


def taiki_floor_name(lay, k):
    return lay.floors[k]["name"] if lay is not None else ""


def taiki_draw_floor(ax, lay, k, dfs=None):
    if lay is None:   # 間取りなし：軌跡の範囲だけで描く
        ax.set_aspect("equal")
        ax.grid(color=GRID, linewidth=0.6)
        ax.set_xlabel("x [m]"); ax.set_ylabel("z [m]")
        return
    img = lay.taiki_floor_raster(k)
    ax.imshow(img, origin="lower", extent=lay.taiki_extent(), cmap=BG_CMAP, vmin=0, vmax=3,
              interpolation="nearest", zorder=0)
    x0, x1, z0, z1 = lay.taiki_floor_bbox(k)
    ax.set_xlim(x0, x1); ax.set_ylim(z0, z1)
    ax.set_aspect("equal")
    ax.set_xlabel("x [m]"); ax.set_ylabel("z [m]")
    ax.grid(color=GRID, linewidth=0.5, zorder=-1)
    for m in lay.taiki_markers_on_floor(k):
        kind = m.get("kind")
        x, z, sx, sz = m["x"], m["z"], max(m.get("sx", 0), 0.6), max(m.get("sz", 0), 0.6)
        if kind == "fire":
            ax.add_patch(Rectangle((x - sx / 2, z - sz / 2), sx, sz, facecolor=FIRE_C, alpha=0.25, edgecolor="none", zorder=2))
            ax.scatter(x, z, marker="^", s=170, color=FIRE_C, edgecolor="white", linewidth=1.2, zorder=7)
            ax.annotate(m.get("label") or "炎", (x, z), xytext=(7, 5), textcoords="offset points",
                        color=FIRE_C, fontsize=9, fontweight="bold", zorder=8)
        elif kind == "obstacle":
            ax.add_patch(Rectangle((x - sx / 2, z - sz / 2), sx, sz, facecolor=OBST_C, edgecolor="none", zorder=2))
            ax.annotate(m.get("label") or "障害物", (x, z + sz / 2), xytext=(0, 3), textcoords="offset points",
                        ha="center", va="bottom", color=OBST_C, fontsize=8, zorder=8)
        elif kind in ("goal", "exit"):
            ax.add_patch(Rectangle((x - sx / 2, z - sz / 2), sx, sz, facecolor=GOAL_C, alpha=0.2, edgecolor=GOAL_C, zorder=2))
            ax.annotate("出口", (x, z), ha="center", va="center", color=GOAL_C, fontsize=9, fontweight="bold", zorder=8)
        elif kind == "sign":
            ax.scatter(x, z, marker="s", s=40, color="#2a78d6", zorder=7)


def taiki_map_legend(lay, extra=()):
    h = list(extra)
    if lay is not None:
        h += [Patch(facecolor="#efeee9", edgecolor="#cfcdc6", label="床"),
              Patch(facecolor="#6b6a66", label="壁・障害物"),
              Patch(facecolor="#cfe0f3", label="階段・段差"),
              Line2D([], [], marker="^", color="w", markerfacecolor=FIRE_C, markersize=10, label="炎")]
    return h


def taiki_floors_used(dfs, lay):
    if lay is None:
        return [0]
    return sorted({int(f) for d in dfs for f in d["floor"].unique()}) or [0]


def taiki_segments(df, k):
    """ 階 k の上にいる区間だけ線分にする（階が変わる所で線を切る）"""
    x, z, f = df["x"].to_numpy(), df["z"].to_numpy(), df["floor"].to_numpy()
    keep = (f[:-1] == k) & (f[1:] == k)
    idx = np.nonzero(keep)[0]
    segs = np.stack([np.c_[x[idx], z[idx]], np.c_[x[idx + 1], z[idx + 1]]], axis=1) if len(idx) else np.zeros((0, 2, 2))
    return segs, idx + 1


def taiki_sp_xy(lay, sp, k):
    on = lay.node_floor[sp] == k
    return np.where(on, lay.node_x[sp], np.nan), np.where(on, lay.node_z[sp], np.nan)


def taiki_map_figure(n_maps, extra_rows=None, lay=None, floors=None):
    """ 地図を n_maps 枚並べる図（2枚以上なら2列。各階の幅に合わせて列幅を決める）"""
    cols = 1 if n_maps == 1 else 2
    rows = math.ceil(n_maps / cols)
    widths = [1] * cols
    aspect = 0.75
    if lay is not None and floors:
        bb = [lay.taiki_floor_bbox(k) for k in floors]
        if n_maps == 2:
            widths = [bb[0][1] - bb[0][0], bb[1][1] - bb[1][0]]
        aspect = max((b[3] - b[2]) / (b[1] - b[0]) for b in bb)
    total_w = 15.0 if cols == 2 else 9.0
    map_h = min(max(total_w / cols * aspect * 1.1, 3.0), 9.0)
    fig = plt.figure(figsize=(total_w, map_h * rows + 1.4 + (2.6 if extra_rows else 0)), layout="constrained")
    ratios = [map_h] * rows + ([2.6] if extra_rows else [])
    gs = fig.add_gridspec(rows + (1 if extra_rows else 0), cols, height_ratios=ratios, width_ratios=widths)
    axes = [fig.add_subplot(gs[r, c]) for r in range(rows) for c in range(cols)][:n_maps]
    return fig, axes, gs


# ------------------------------------------------------------------ A. 経路
def taiki_fig_trajectory(df, lay, out):
    floors = taiki_floors_used([df], lay)
    fig, axes, _ = taiki_map_figure(len(floors), lay=lay, floors=floors)
    tmax = df["elapsed_sec"].max()
    lc = None
    for ax, k in zip(axes, floors):
        taiki_draw_floor(ax, lay, k)
        sp = df.attrs.get("sp_nodes")
        if sp is not None:
            ax.plot(*taiki_sp_xy(lay, sp, k), color=TEXT, linestyle=(0, (3, 2)), linewidth=1.5, zorder=4)
        segs, idx = taiki_segments(df, k)
        if len(segs):
            lc = LineCollection(segs, cmap=TIME_CMAP, linewidth=2.6, zorder=5, capstyle="round")
            lc.set_array(df["elapsed_sec"].to_numpy()[idx]); lc.set_clim(0, tmax)
            ax.add_collection(lc)
        if df["floor"].iloc[0] == k:
            ax.scatter(df["x"].iloc[0], df["z"].iloc[0], s=80, color=TEXT, edgecolor="white", linewidth=1.5, zorder=9)
        if df["floor"].iloc[-1] == k:
            ax.scatter(df["x"].iloc[-1], df["z"].iloc[-1], marker="*", s=220, color=TEXT, edgecolor="white", linewidth=1.2, zorder=9)
        ax.set_title(taiki_floor_name(lay, k), loc="left")
        if lay is None:
            ax.autoscale()
    if lc is not None:
        cb = fig.colorbar(lc, ax=axes, shrink=0.6)
        cb.set_label("経過時間 [s]"); cb.outline.set_visible(False)
    m = taiki_metrics(df)
    handles = taiki_map_legend(lay, [
        Line2D([], [], color="#2a78d6", linewidth=2.6, label="実際の経路"),
        Line2D([], [], color=TEXT, linestyle=(0, (3, 2)), linewidth=1.5, label="最短経路"),
        Line2D([], [], marker="o", color="w", markerfacecolor=TEXT, markersize=8, label="スタート"),
        Line2D([], [], marker="*", color="w", markerfacecolor=TEXT, markersize=13, label="記録終了")])
    fig.legend(handles=handles, loc="outside lower center", ncol=8, frameon=False)
    sp_txt = f"（最短 {m['最短距離[m]']} m）" if np.isfinite(m["最短距離[m]"]) else ""
    fig.suptitle(f"移動経路（階ごと）　条件 {m['condition']} / {m['participant']}　"
                 f"脱出 {m['脱出時間[s]']} 秒・移動 {m['移動距離[m]']} m{sp_txt}", x=0.01, ha="left", fontweight="bold", fontsize=13)
    fig.savefig(out, bbox_inches="tight")
    plt.close(fig)


def taiki_fig_trajectory_all(dfs, lay, conds, out):
    cmap = taiki_cond_colors(conds)
    floors = taiki_floors_used(dfs, lay)
    fig, axes, _ = taiki_map_figure(len(floors), lay=lay, floors=floors)
    for ax, k in zip(axes, floors):
        taiki_draw_floor(ax, lay, k)
        for d in dfs:
            segs, _ = taiki_segments(d, k)
            if len(segs):
                ax.add_collection(LineCollection(segs, colors=cmap[d.attrs["condition"]], linewidth=1.8, alpha=0.65, zorder=5))
            if d["floor"].iloc[-1] == k:
                ax.scatter(d["x"].iloc[-1], d["z"].iloc[-1], marker="*", s=110, color=cmap[d.attrs["condition"]],
                           edgecolor="white", linewidth=0.8, zorder=9)
        ax.set_title(taiki_floor_name(lay, k), loc="left")
        if lay is None:
            ax.autoscale()
    handles = [Line2D([], [], color=cmap[c], linewidth=2.5, label=f"条件 {c}") for c in conds]
    fig.legend(handles=taiki_map_legend(lay, handles), loc="outside lower center", ncol=9, frameon=False)
    fig.suptitle(f"全プレイの移動経路（{len(dfs)} プレイ、色＝条件、★＝記録終了地点）", x=0.01, ha="left", fontweight="bold", fontsize=13)
    fig.savefig(out, bbox_inches="tight")
    plt.close(fig)


# ------------------------------------------------------------------ B. ヒートマップ
def taiki_dwell(dfs, lay, k, bbox, cell=0.5):
    x0, x1, z0, z1 = bbox
    xe, ze = np.arange(x0, x1 + cell, cell), np.arange(z0, z1 + cell, cell)
    H = np.zeros((len(ze) - 1, len(xe) - 1))
    for d in dfs:
        m = (d["floor"] == k).to_numpy()
        h, _, _ = np.histogram2d(d["z"][m], d["x"][m], bins=[ze, xe], weights=d["dt"][m])
        H += h / max(len(dfs), 1)
    k1 = np.exp(-0.5 * (np.arange(-3, 4) / 1.0) ** 2); k1 /= k1.sum()   # 軽くぼかす（合計は保存）
    H = np.apply_along_axis(lambda v: np.convolve(np.pad(v, 3), k1, mode="valid"), 0, H)
    H = np.apply_along_axis(lambda v: np.convolve(np.pad(v, 3), k1, mode="valid"), 1, H)
    return H, xe, ze


def taiki_fig_heatmap(dfs, lay, conds, out):
    floors = taiki_floors_used(dfs, lay)
    if lay is not None:
        bboxes = {k: lay.taiki_floor_bbox(k) for k in floors}
    else:
        ax_ = np.concatenate([d["x"] for d in dfs]); az_ = np.concatenate([d["z"] for d in dfs])
        bboxes = {0: (ax_.min() - 1, ax_.max() + 1, az_.min() - 1, az_.max() + 1)}
    grids = {(c, k): taiki_dwell([d for d in dfs if d.attrs["condition"] == c], lay, k, bboxes[k]) for c in conds for k in floors}
    vmax = max(g[0].max() for g in grids.values()) or 1
    widths = [bboxes[k][1] - bboxes[k][0] for k in floors]
    aspect = max((bboxes[k][3] - bboxes[k][2]) / (bboxes[k][1] - bboxes[k][0]) for k in floors)
    total_w = 15.0
    row_h = min(max(total_w * min(widths) / sum(widths) * aspect * 1.15, 2.6), 7.0)
    fig, axes = plt.subplots(len(conds), len(floors), figsize=(total_w + 1.2, row_h * len(conds) + 1.0),
                             squeeze=False, layout="constrained", gridspec_kw=dict(width_ratios=widths))
    im = None
    for r, c in enumerate(conds):
        n = sum(d.attrs["condition"] == c for d in dfs)
        for ci, k in enumerate(floors):
            ax = axes[r][ci]
            taiki_draw_floor(ax, lay, k)
            H, xe, ze = grids[(c, k)]
            im = ax.pcolormesh(xe, ze, np.ma.masked_where(H <= vmax * 0.02, H), cmap=HEAT_CMAP, vmin=0, vmax=vmax, zorder=3)
            ax.set_title(f"条件 {c}（{n} 人平均）　{taiki_floor_name(lay, k)}", loc="left")
            if r < len(conds) - 1:
                ax.set_xlabel("")
    cb = fig.colorbar(im, ax=axes.ravel().tolist(), shrink=0.4)
    cb.set_label("1人あたり滞在時間 [s]（0.5m 四方）"); cb.outline.set_visible(False)
    fig.suptitle("通った場所のヒートマップ（濃い所＝長く留まった・迷った場所。全条件で同じ色の尺度）",
                 x=0.01, ha="left", fontweight="bold", fontsize=13)
    fig.savefig(out, bbox_inches="tight")
    plt.close(fig)


# ------------------------------------------------------------------ C. 最短経路との比較
def taiki_fig_route_compare(df, lay, out):
    floors = taiki_floors_used([df], lay)
    fig, axes, gs = taiki_map_figure(len(floors), extra_rows=True, lay=lay, floors=floors)
    sp = df.attrs["sp_nodes"]
    dev_seg = df["dev"].to_numpy()
    for ax, k in zip(axes, floors):
        taiki_draw_floor(ax, lay, k)
        ax.plot(*taiki_sp_xy(lay, sp, k), color=GOAL_C, linewidth=6, alpha=0.35, solid_capstyle="round", zorder=4)
        segs, idx = taiki_segments(df, k)
        if len(segs):
            off = dev_seg[idx] > OFF_ROUTE_M
            ax.add_collection(LineCollection(segs[~off], colors="#2a78d6", linewidth=2.2, zorder=5))
            ax.add_collection(LineCollection(segs[off], colors="#e34948", linewidth=2.4, zorder=6))
        ax.set_title(taiki_floor_name(lay, k), loc="left")
    ax2 = fig.add_subplot(gs[-1, :])
    t, dev = df["elapsed_sec"], df["dev"]
    ax2.fill_between(t, 0, dev, where=dev > OFF_ROUTE_M, color="#e34948", alpha=0.18, linewidth=0, step="mid")
    ax2.plot(t, dev, color="#52514e", linewidth=1.5)
    ax2.axhline(OFF_ROUTE_M, color=MUTED, linestyle="--", linewidth=1)
    ax2.text(t.iloc[0], OFF_ROUTE_M, f" {OFF_ROUTE_M} m 以上＝外れている", va="bottom", color=MUTED, fontsize=9)
    ax2.set_xlabel("経過時間 [s]"); ax2.set_ylabel("最短経路からの\nズレ [m]")
    ax2.set_ylim(bottom=0); ax2.set_xlim(t.min(), t.max())
    ax2.grid(axis="y", color=GRID, linewidth=0.6)
    mt = taiki_metrics(df)
    handles = taiki_map_legend(lay, [
        Line2D([], [], color=GOAL_C, linewidth=6, alpha=0.35, label="最短経路"),
        Line2D([], [], color="#2a78d6", linewidth=2.2, label="実際（最短経路の近く）"),
        Line2D([], [], color="#e34948", linewidth=2.4, label=f"実際（{OFF_ROUTE_M}m以上外れた区間）")])
    fig.legend(handles=handles, loc="outside lower center", ncol=7, frameon=False)
    fig.suptitle(f"最短経路と実際の経路　条件 {mt['condition']} / {mt['participant']}　"
                 f"実際 {mt['移動距離[m]']} m ／ 最短 {mt['最短距離[m]']} m（余分 {mt['余分な距離[m]']} m・迂回率 {mt['迂回率']}）"
                 f"・外れていた時間 {mt['最短経路から外れた時間[s]']} 秒", x=0.01, ha="left", fontweight="bold", fontsize=12)
    fig.savefig(out, bbox_inches="tight")
    plt.close(fig)


def taiki_fig_route_excess(summary, conds, out):
    cmap = taiki_cond_colors(conds)
    cols = [("余分な距離[m]", "余分に歩いた距離 [m]"), ("迂回率", "迂回率（実際 ÷ 最短。点線 1.0＝最短）"),
            ("最短経路から外れた時間[s]", "最短経路から外れていた時間 [s]")]
    fig, axes = plt.subplots(1, 3, figsize=(15, 4.8), layout="constrained")
    rng = np.random.default_rng(0)
    for ax, (col, lab) in zip(axes, cols):
        top = 0
        for i, c in enumerate(conds):
            v = summary.loc[summary["condition"] == c, col].dropna().to_numpy(dtype=float)
            if len(v) == 0:
                continue
            mean = v.mean()
            se = v.std(ddof=1) / math.sqrt(len(v)) if len(v) > 1 else 0
            ax.bar(i, mean, width=0.6, color=cmap[c], alpha=0.85, zorder=2)
            if se:
                ax.errorbar(i, mean, yerr=se, color="#52514e", capsize=5, linewidth=1.4, zorder=3)
            ax.scatter(i + rng.uniform(-0.13, 0.13, len(v)), v, s=26, color="white", edgecolor="#52514e", linewidth=1, zorder=4)
            peak = max(v.max(), mean + se)
            top = max(top, peak)
            ax.text(i, peak, ("%.2f" if col == "迂回率" else "%.1f") % mean + "\n", ha="center", va="bottom",
                    fontsize=10, fontweight="bold", color=TEXT, linespacing=0.9)
        if col == "迂回率":
            ax.axhline(1.0, color=MUTED, linewidth=1, linestyle="--")
        ax.set_ylim(0, top * 1.25 if top else 1)
        ax.set_xticks(range(len(conds))); ax.set_xticklabels([f"条件 {c}" for c in conds])
        ax.set_title(lab, loc="left")
        ax.grid(axis="y", color=GRID, linewidth=0.6, zorder=0)
    fig.suptitle("最短経路と実際の経路の差（棒＝平均、線＝標準誤差、○＝各プレイ）", x=0.01, ha="left", fontweight="bold", fontsize=13)
    fig.savefig(out, bbox_inches="tight")
    plt.close(fig)


# ------------------------------------------------------------------ D. ゴールまでの距離の時系列
def taiki_fig_distance_ts(dfs, conds, out, euclid=False):
    cmap = taiki_cond_colors(conds)
    tmax = max(d["elapsed_sec"].max() for d in dfs)
    grid = np.arange(0, tmax + TS_STEP, TS_STEP)
    fig, ax = plt.subplots(figsize=(11, 5.4), layout="constrained")
    for c in conds:
        series = []
        for d in dfs:
            if d.attrs["condition"] != c:
                continue
            t, y = d["elapsed_sec"].to_numpy(), d["dist_goal"].to_numpy(dtype=float)
            ok = np.isfinite(y)
            if ok.sum() < 2:
                continue
            yi = np.interp(grid, t[ok], y[ok])
            yi[grid > t[ok][-1]] = 0.0 if y[ok][-1] < 2.0 else y[ok][-1]   # 記録終了後：ゴールなら0、未到達なら最後の値
            series.append(yi)
            ax.plot(grid, yi, color=cmap[c], linewidth=0.8, alpha=0.22, zorder=2)
        if not series:
            continue
        S = np.vstack(series)
        mean = S.mean(axis=0)
        se = S.std(axis=0, ddof=1) / math.sqrt(len(S)) if len(S) > 1 else np.zeros_like(mean)
        ax.fill_between(grid, np.maximum(mean - se, 0), mean + se, color=cmap[c], alpha=0.16, linewidth=0, zorder=3)
        ax.plot(grid, mean, color=cmap[c], linewidth=2.6, zorder=4, label=f"条件 {c}（n={len(S)}）")
    ax.set_xlabel("経過時間 [s]")
    ax.set_ylabel("ゴールまでの距離 [m]" + ("（直線距離）" if euclid else "（壁を通らない道のり）"))
    ax.set_xlim(0, tmax); ax.set_ylim(bottom=0)
    ax.grid(color=GRID, linewidth=0.6)
    ax.legend(frameon=False, loc="upper right")
    ax.set_title("ゴールまでの距離の時間変化（太線＝条件ごとの平均、帯＝標準誤差、細線＝各協力者）", loc="left")
    fig.savefig(out, bbox_inches="tight")
    plt.close(fig)


# ------------------------------------------------------------------ E. 表
def taiki_fig_table(summary, out):
    cols = ["condition", "participant", "脱出時間[s]", "ゴール到達", "移動距離[m]", "最短距離[m]", "余分な距離[m]",
            "迂回率", "最短経路から外れた時間[s]", "立ち止まり時間[s]", "平均速度[m/s]"]
    num = [c for c in cols[2:] if c != "ゴール到達"]
    tbl = summary[cols].copy()
    g = summary.groupby("condition", sort=True)[num].mean().round(2).reset_index()
    g.insert(1, "participant", "平均")
    tbl = pd.concat([tbl, g], ignore_index=True)
    tbl = tbl.astype(object).where(tbl.notna(), "")
    labels = {"condition": "条件", "participant": "協力者", "最短経路から外れた時間[s]": "外れた時間[s]"}
    head = [labels.get(c, c).replace("[", "\n[") for c in tbl.columns]
    fig, ax = plt.subplots(figsize=(14, 0.32 * (len(tbl) + 2) + 0.5))
    ax.axis("off")
    t = ax.table(cellText=tbl.values, colLabels=head, loc="upper center", cellLoc="center",
                 colWidths=[0.06, 0.08] + [0.085] * (len(head) - 2))
    t.auto_set_font_size(False); t.set_fontsize(9.5); t.scale(1, 1.4)
    for (r, c), cell in t.get_celld().items():
        cell.set_edgecolor(GRID)
        if r == 0:
            cell.set_facecolor("#2a78d6"); cell.set_text_props(color="white", fontweight="bold")
            cell.set_height(cell.get_height() * 1.8)
        elif tbl.iloc[r - 1]["participant"] == "平均":
            cell.set_facecolor("#eef4fc"); cell.set_text_props(fontweight="bold")
    ax.set_title(f"プレイごとの指標（外れた＝最短経路から {OFF_ROUTE_M}m 以上、立ち止まり＝{STOP_SPEED}m/s 未満）",
                 loc="left", pad=10, color=TEXT)
    fig.savefig(out, bbox_inches="tight")
    plt.close(fig)


# ------------------------------------------------------------------ main
def main():
    ap = argparse.ArgumentParser(description="避難ログCSVから図・表を作る")
    ap.add_argument("inputs", nargs="+", help="CSVファイル / フォルダ / ワイルドカード")
    ap.add_argument("--layout", help="間取りJSON（Tools > taiki > 間取りを書き出す）")
    ap.add_argument("--goal", help="ゴール位置 x,y,z（間取りJSONに GoalZone が無いとき／間取りなしのとき）")
    ap.add_argument("--height-offset", type=float, default=1.2,
                    help="ログの y から床の高さを推定するとき引く値（--scale で直した後の m）。既定 1.2（頭の位置でも体の中心でも可）")
    ap.add_argument("--scale", type=float, default=1.0,
                    help="Unityの1単位が何mか。実験場（講義棟）は約3.5倍の縮尺なので 0.29 など。既定 1.0")
    ap.add_argument("--out", default="figures")
    ap.add_argument("--no-per-play", action="store_true", help="1プレイごとの図（A1, C1）を作らない")
    args = ap.parse_args()

    files = taiki_collect(args.inputs)
    if not files:
        sys.exit("CSVが見つかりません")
    os.makedirs(args.out, exist_ok=True)
    goal = tuple(float(v) * args.scale for v in args.goal.split(",")) if args.goal else None
    lay = taiki_Layout(args.layout, goal_override=goal, scale=args.scale) if args.layout else None
    if lay is not None:
        print(f"間取り: {lay.scene}  階 {[f['name'] for f in lay.floors]}  歩ける点 {int(lay.reachable.sum())}  ゴール点 {len(lay.goal_nodes)}")
        if lay.dist_to_goal is None:
            print("[注意] ゴールが見つからないので、最短経路とゴール距離は計算できません（--goal x,y,z で指定できます）")

    dfs = []
    for f in files:
        try:
            d = taiki_load_csv(f, args.scale)
            if len(d) >= 2:
                dfs.append(taiki_enrich(d, lay, args.height_offset))
        except Exception as e:
            print(f"[skip] {f}: {e}")
    if not dfs:
        sys.exit("読み込めるCSVがありません")
    euclid = False
    if (lay is None or lay.dist_to_goal is None) and goal is not None:   # 間取りなし：直線距離で代用
        for d in dfs:
            d["dist_goal"] = np.hypot(d["x"] - goal[0], d["z"] - goal[2])
        euclid = True
    conds = sorted({d.attrs["condition"] for d in dfs})
    print(f"{len(dfs)} プレイ / 条件 {conds}")

    has_sp = lay is not None and lay.dist_to_goal is not None
    if not args.no_per_play:
        for d in dfs:
            taiki_fig_trajectory(d, lay, os.path.join(args.out, f"A1_trajectory_{taiki_tag(d)}.png"))
            if has_sp and d.attrs["sp_nodes"] is not None:
                taiki_fig_route_compare(d, lay, os.path.join(args.out, f"C1_route_compare_{taiki_tag(d)}.png"))
    taiki_fig_trajectory_all(dfs, lay, conds, os.path.join(args.out, "A2_trajectory_all.png"))
    taiki_fig_heatmap(dfs, lay, conds, os.path.join(args.out, "B1_heatmap_by_condition.png"))
    summary = pd.DataFrame([taiki_metrics(d) for d in dfs])
    if has_sp:
        taiki_fig_route_excess(summary, conds, os.path.join(args.out, "C2_route_excess_by_condition.png"))
    if np.isfinite(np.concatenate([d["dist_goal"].to_numpy(dtype=float) for d in dfs])).any():
        taiki_fig_distance_ts(dfs, conds, os.path.join(args.out, "D1_distance_to_goal_timeseries.png"), euclid)
    else:
        print("[注意] ゴール距離が無いので D1 は作りません（--layout か --goal を指定してください）")
    summary.to_csv(os.path.join(args.out, "summary.csv"), index=False, encoding="utf-8-sig")
    taiki_fig_table(summary, os.path.join(args.out, "E1_summary_table.png"))
    print(summary.drop(columns=["session"]).to_string(index=False))
    print(f"→ {os.path.abspath(args.out)}")


if __name__ == "__main__":
    main()
