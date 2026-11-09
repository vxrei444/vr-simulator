"""
避難VRログの分析・可視化スクリプト
EvacuationTrajectoryLogger が出力した *_track.csv / *_events.csv を読み、
  ・セッションごとの指標（脱出時間・移動距離・停止/迷い回数・看板注視など）
  ・軌跡マップ（時間で色分け＋迷い地点＋顔の向き）
  ・滞在ヒートマップ
  ・顔の向き(yaw)と速度の時系列
  ・条件（矢印デザイン）ごとの比較箱ひげ図
を出力する。

使い方:
  pip install pandas numpy matplotlib
  python analyze_evac_logs.py ログフォルダ [--out 出力フォルダ]
         [--floorplan 平面図.png --extent xmin xmax zmin zmax]

座標: Unity の X(東西) と Z(南北) を上から見た図にする（Y は高さ）。
"""
import argparse
import glob
import os

import numpy as np
import pandas as pd
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.collections import LineCollection

plt.rcParams["font.family"] = ["Yu Gothic", "Meiryo", "Hiragino Sans", "Noto Sans CJK JP", "Noto Serif CJK JP", "IPAexGothic", "DejaVu Sans"]

# ---- 迷い判定のしきい値（実験に合わせて調整） ----
STOP_SPEED = 0.15        # m/s 未満を「停止」
STOP_MIN_DUR = 1.0       # 秒 以上続いたら停止エピソード
LOOK_YAW_RANGE = 60.0    # 停止中に頭を左右に振った幅(度)がこれ以上なら「見回し＝迷い」
TURNBACK_DEG = 135.0     # 進行方向がこの角度以上反転したら「引き返し」
TURNBACK_DIST = 1.0      # m: 反転判定に使う前後の移動距離
REVISIT_GAP = 5.0        # 秒: このマスを離れてから戻るまでの最小時間（ゆらぎ除外）
GRID = 0.5               # m: ヒートマップ/再訪判定のマス目


def load_sessions(log_dir):
    sessions = []
    for tp in sorted(glob.glob(os.path.join(log_dir, "**", "*_track.csv"), recursive=True)):
        base = os.path.basename(tp)[:-len("_track.csv")]
        parts = base.split("_")  # yyyyMMdd_HHmmss_participant_condition
        participant = parts[2] if len(parts) > 2 else "NA"
        condition = "_".join(parts[3:]) if len(parts) > 3 else "NA"
        tr = pd.read_csv(tp, keep_default_na=False, na_values=[""])
        ep = tp.replace("_track.csv", "_events.csv")
        ev = pd.read_csv(ep, keep_default_na=False, na_values=[""]) if os.path.exists(ep) else pd.DataFrame()
        if len(tr) < 2:
            continue
        sessions.append(dict(name=base, participant=participant, condition=condition, track=tr, events=ev))
    return sessions


def unwrap_deg(a):
    return np.degrees(np.unwrap(np.radians(a)))


def detect_stops(tr):
    """停止エピソードを検出し、そのうち見回しを伴うものを『迷い』とする"""
    t = tr["t"].to_numpy()
    slow = tr["speed_mps"].rolling(5, center=True, min_periods=1).mean().to_numpy() < STOP_SPEED
    yaw = unwrap_deg(tr["yaw_deg"].to_numpy())
    out = []
    i = 0
    while i < len(t):
        if slow[i]:
            j = i
            while j + 1 < len(t) and slow[j + 1]:
                j += 1
            dur = t[j] - t[i]
            if dur >= STOP_MIN_DUR:
                yr = float(yaw[i:j + 1].max() - yaw[i:j + 1].min())
                out.append(dict(t_start=t[i], t_end=t[j], duration=dur,
                                x=tr["pos_x"].iloc[i:j + 1].mean(), z=tr["pos_z"].iloc[i:j + 1].mean(),
                                yaw_range=yr, hesitation=yr >= LOOK_YAW_RANGE))
            i = j + 1
        else:
            i += 1
    return pd.DataFrame(out, columns=["t_start", "t_end", "duration", "x", "z", "yaw_range", "hesitation"])


def detect_turnbacks(tr):
    """進行方向の反転（引き返し）を検出。停止の影響を受けないよう、移動距離ごとに再サンプルして判定"""
    t = tr["t"].to_numpy(); x = tr["pos_x"].to_numpy(); z = tr["pos_z"].to_numpy()
    seg = np.hypot(np.diff(x), np.diff(z)); seg[seg > 3] = 0
    s = np.concatenate([[0], np.cumsum(seg)])
    if s[-1] < 2 * TURNBACK_DIST:
        return pd.DataFrame(columns=["t", "x", "z", "angle"])
    sg = np.arange(0, s[-1], 0.1)                     # 10cm 間隔
    idx = np.clip(np.searchsorted(s, sg), 0, len(s) - 1)
    xs, zs, ts = x[idx], z[idx], t[idx]
    k = int(TURNBACK_DIST / 0.1)
    res, last_s = [], -999
    for i in range(k, len(sg) - k):
        a = np.array([xs[i] - xs[i - k], zs[i] - zs[i - k]])
        b = np.array([xs[i + k] - xs[i], zs[i + k] - zs[i]])
        na, nb = np.linalg.norm(a), np.linalg.norm(b)
        if na < 0.5 * TURNBACK_DIST or nb < 0.5 * TURNBACK_DIST:
            continue
        ang = np.degrees(np.arccos(np.clip(a @ b / (na * nb), -1, 1)))
        if ang >= TURNBACK_DEG and sg[i] - last_s > 2 * TURNBACK_DIST:
            res.append(dict(t=ts[i], x=xs[i], z=zs[i], angle=ang))
            last_s = sg[i]
    return pd.DataFrame(res, columns=["t", "x", "z", "angle"])


def session_metrics(s):
    tr, ev = s["track"], s["events"]
    d = np.hypot(np.diff(tr["pos_x"]), np.diff(tr["pos_z"]))
    path_len = float(d[d < 3].sum())  # テレポート等の飛びは除外
    stops = detect_stops(tr)
    turns = detect_turnbacks(tr)
    cells = list(zip((tr["pos_x"] // GRID).astype(int), (tr["pos_z"] // GRID).astype(int)))
    # 再訪: 一度離れたマスに REVISIT_GAP 秒以上たってから戻ってきた回数
    last_seen, prev, revisits = {}, None, 0
    for c, tt in zip(cells, tr["t"].to_numpy()):
        if c != prev and c in last_seen and tt - last_seen[c] > REVISIT_GAP:
            revisits += 1
        last_seen[c] = tt; prev = c
    result, escape = "unknown", float(tr["t"].iloc[-1])
    sign_fix = 0; dp_wrong = 0; dp_total = 0
    if len(ev):
        end = ev[ev["event"] == "session_end"]
        if len(end):
            result = str(end["id"].iloc[-1])
            try:
                escape = float(end["detail"].iloc[-1])
            except (ValueError, TypeError):
                pass
        sign_fix = int((ev["event"] == "sign_gaze_start").sum())
        dpx = ev[ev["event"] == "dp_exit"]
        dp_total = len(dpx)
        dp_wrong = int(dpx["detail"].astype(str).str.contains(";wrong;").sum())
    yaw = unwrap_deg(tr["yaw_deg"].to_numpy())
    dt = np.diff(tr["t"].to_numpy()); dt[dt <= 0] = np.nan
    yaw_speed = np.nanmean(np.abs(np.diff(yaw)) / dt)
    gaze_sign_time = float((tr["gaze_sign_id"].notna()).sum() * np.nanmedian(dt)) if "gaze_sign_id" in tr else 0.0
    # ---- 煙・症状（SmokeExposure の列があるとき） ----
    smoke = {}
    if "k_head" in tr.columns:
        dts = np.median(np.diff(tr["t"].to_numpy())) if len(tr) > 1 else 0.1
        smoke = dict(
            time_in_smoke_s=round(float((tr["in_smoke"] == 1).sum() * dts), 2),
            max_k_head=round(float(tr["k_head"].max()), 3),
            final_fed=round(float(tr["fed"].iloc[-1]), 4),
            final_dose=round(float(tr["dose"].iloc[-1]), 2),
            max_stage=int(tr["stage"].max()),
            crouch_time_s=round(float((tr["head_above_floor_m"] < 1.2).sum() * dts), 2),
            min_visibility_m=round(float((8.0 / tr["k_head"].clip(lower=1e-3)).min()), 2),
        )
        if len(ev):
            smoke["incapacitated"] = int((ev["event"] == "incapacitated").any())
            smoke["cough_count"] = int((ev["event"] == "cough").sum())
        if smoke.get("incapacitated"):
            result = "incapacitated"
    return dict(session=s["name"], participant=s["participant"], condition=s["condition"], result=result,
                escape_time_s=round(escape, 2), path_length_m=round(path_len, 2),
                mean_speed_mps=round(path_len / max(escape, 1e-3), 3),
                stop_count=len(stops), stop_time_s=round(float(stops["duration"].sum()) if len(stops) else 0, 2),
                hesitation_count=int(stops["hesitation"].sum()) if len(stops) else 0,
                turnback_count=len(turns), revisit_cells=revisits,
                mean_head_yaw_speed_dps=round(float(yaw_speed), 1),
                sign_fixations=sign_fix, sign_gaze_time_s=round(gaze_sign_time, 2),
                decision_wrong=dp_wrong, decision_total=dp_total, **smoke), stops, turns


def draw_background(ax, floorplan, extent):
    if floorplan and extent:
        ax.imshow(plt.imread(floorplan), extent=extent, origin="upper", alpha=0.5, zorder=0)


def plot_session(s, stops, turns, out, floorplan=None, extent=None):
    tr, ev = s["track"], s["events"]
    x, z, t = tr["pos_x"].to_numpy(), tr["pos_z"].to_numpy(), tr["t"].to_numpy()
    fig, axes = plt.subplots(1, 2, figsize=(15, 7))

    # --- 軌跡マップ ---
    ax = axes[0]
    draw_background(ax, floorplan, extent)
    pts = np.column_stack([x, z]).reshape(-1, 1, 2)
    lc = LineCollection(np.concatenate([pts[:-1], pts[1:]], axis=1), cmap="viridis", linewidths=2.5, zorder=2)
    lc.set_array(t[:-1]); ax.add_collection(lc)
    fig.colorbar(lc, ax=ax, label="経過時間 [s]")
    # 顔の向き（1秒ごと）
    step = max(1, int(round(1.0 / max(np.median(np.diff(t)), 1e-3))))
    ax.quiver(x[::step], z[::step], tr["gaze_x"].to_numpy()[::step], tr["gaze_z"].to_numpy()[::step],
              color="gray", alpha=0.6, scale=25, width=0.003, zorder=3, label="顔の向き(1秒毎)")
    if len(stops):
        h = stops[stops["hesitation"]]; n = stops[~stops["hesitation"]]
        ax.scatter(n["x"], n["z"], s=n["duration"] * 40, facecolors="none", edgecolors="orange", lw=1.5, zorder=4, label="停止")
        ax.scatter(h["x"], h["z"], s=h["duration"] * 40, c="red", alpha=0.6, zorder=5, label="迷い(停止+見回し)")
        for _, r in h.iterrows():
            ax.annotate(f"{r['duration']:.1f}s", (r["x"], r["z"]), fontsize=8, color="red", xytext=(4, 4), textcoords="offset points")
    if len(turns):
        ax.scatter(turns["x"], turns["z"], marker="x", c="magenta", s=80, zorder=5, label="引き返し")
    ax.scatter(x[0], z[0], marker="^", s=150, c="green", zorder=6, label="スタート")
    ax.scatter(x[-1], z[-1], marker="*", s=220, c="blue", zorder=6, label="終了")
    if len(ev) and "sign_gaze_start" in set(ev["event"]):
        sg = ev[ev["event"] == "sign_gaze_start"]
        ax.scatter(sg["pos_x"], sg["pos_z"], marker="o", s=25, c="cyan", edgecolors="k", zorder=6, label="看板を注視した位置")
    ax.set_aspect("equal"); ax.autoscale()
    ax.set_xlabel("X [m]"); ax.set_ylabel("Z [m]")
    ax.set_title(f"軌跡  {s['participant']} / 条件 {s['condition']}")
    ax.legend(fontsize=8, loc="best")

    # --- 滞在ヒートマップ ---
    ax = axes[1]
    draw_background(ax, floorplan, extent)
    xb = np.arange(np.floor(x.min()) - GRID, np.ceil(x.max()) + GRID * 2, GRID)
    zb = np.arange(np.floor(z.min()) - GRID, np.ceil(z.max()) + GRID * 2, GRID)
    dt = np.median(np.diff(t))
    H, _, _ = np.histogram2d(x, z, bins=[xb, zb])
    H = np.ma.masked_equal(H.T * dt, 0)
    m = ax.pcolormesh(xb, zb, H, cmap="hot_r", zorder=1, alpha=0.85)
    fig.colorbar(m, ax=ax, label="滞在時間 [s]")
    ax.plot(x, z, color="k", lw=0.5, alpha=0.4, zorder=2)
    ax.set_aspect("equal"); ax.set_xlabel("X [m]"); ax.set_ylabel("Z [m]")
    ax.set_title(f"滞在ヒートマップ ({GRID}m格子)")
    fig.tight_layout(); fig.savefig(os.path.join(out, f"{s['name']}_map.png"), dpi=150); plt.close(fig)

    # --- 時系列: 速度・顔の向き・看板注視 ---
    has_smoke = "k_head" in tr.columns
    if has_smoke:
        fig, (a1, a2, a3) = plt.subplots(3, 1, figsize=(13, 9), sharex=True)
    else:
        fig, (a1, a2) = plt.subplots(2, 1, figsize=(13, 6), sharex=True)
    a1.plot(t, tr["speed_mps"].rolling(5, center=True, min_periods=1).mean(), lw=1)
    a1.axhline(STOP_SPEED, ls="--", c="gray", lw=0.8); a1.set_ylabel("速度 [m/s]")
    for _, r in stops.iterrows():
        for a in (a1, a2):
            a.axvspan(r["t_start"], r["t_end"], color="red" if r["hesitation"] else "orange", alpha=0.2)
    a2.plot(t, unwrap_deg(tr["yaw_deg"].to_numpy()), lw=1, label="yaw(左右)")
    a2.plot(t, tr["pitch_deg"], lw=1, alpha=0.7, label="pitch(上下, 下向き+)")
    if "gaze_sign_id" in tr:
        on = tr["gaze_sign_id"].notna().to_numpy()
        a2.fill_between(t, a2.get_ylim()[0], a2.get_ylim()[1], where=on, color="cyan", alpha=0.25, label="看板を見ている")
    a2.set_ylabel("頭の角度 [deg]"); a2.set_xlabel("経過時間 [s]"); a2.legend(fontsize=8)
    if has_smoke:
        a3.plot(t, tr["smoke_layer_m"], c="k", lw=1.5, label="煙層の下端の高さ")
        a3.plot(t, tr["head_above_floor_m"], c="tab:blue", lw=1, label="頭の高さ")
        a3.fill_between(t, 0, 3, where=(tr["in_smoke"] == 1).to_numpy(), color="gray", alpha=0.3, label="頭が煙の中")
        a3.set_ylim(0, 3); a3.set_ylabel("高さ [m]"); a3.set_xlabel("経過時間 [s]")
        ax3 = a3.twinx()
        ax3.step(t, tr["stage"], c="red", where="post", lw=1, label="症状の段階")
        ax3.set_ylim(-0.2, 3.5); ax3.set_ylabel("症状の段階", color="red")
        a3.legend(fontsize=8, loc="upper left")
    a1.set_title(f"速度と頭の向き  {s['participant']} / {s['condition']}（赤=迷い, 橙=停止）")
    fig.tight_layout(); fig.savefig(os.path.join(out, f"{s['name']}_timeline.png"), dpi=150); plt.close(fig)


def plot_overlay(sessions, out, floorplan=None, extent=None):
    """全参加者の軌跡を条件ごとに重ねる"""
    conds = sorted({s["condition"] for s in sessions})
    fig, axes = plt.subplots(1, len(conds), figsize=(6 * len(conds), 6), squeeze=False)
    for ax, c in zip(axes[0], conds):
        draw_background(ax, floorplan, extent)
        for s in sessions:
            if s["condition"] == c:
                ax.plot(s["track"]["pos_x"], s["track"]["pos_z"], lw=1.2, alpha=0.6, label=s["participant"])
        ax.set_aspect("equal"); ax.set_title(f"条件 {c}"); ax.legend(fontsize=7)
    fig.tight_layout(); fig.savefig(os.path.join(out, "overlay_by_condition.png"), dpi=150); plt.close(fig)


def plot_compare(df, out):
    cols = [("escape_time_s", "脱出時間 [s]"), ("path_length_m", "移動距離 [m]"),
            ("time_in_smoke_s", "頭が煙の中にいた時間 [s]"), ("crouch_time_s", "しゃがんだ時間 [s]"),
            ("hesitation_count", "迷い回数"), ("stop_time_s", "停止時間合計 [s]"),
            ("turnback_count", "引き返し回数"), ("sign_fixations", "看板注視回数")]
    conds = sorted(df["condition"].unique())
    cols = [c for c in cols if c[0] in df.columns]
    nrow = (len(cols) + 2) // 3
    fig, axes = plt.subplots(nrow, 3, figsize=(14, 4 * nrow), squeeze=False)
    for ax, (c, lab) in zip(axes.ravel(), cols):
        data = [df[df["condition"] == k][c].to_numpy() for k in conds]
        ax.boxplot(data, showmeans=True)
        ax.set_xticks(range(1, len(conds) + 1), conds)
        for i, d in enumerate(data):
            ax.scatter(np.full(len(d), i + 1) + np.random.uniform(-0.08, 0.08, len(d)), d, s=15, alpha=0.6)
        ax.set_title(lab)
    for ax in axes.ravel()[len(cols):]:
        ax.axis("off")
    fig.suptitle("条件（誘導デザイン）別の比較"); fig.tight_layout()
    fig.savefig(os.path.join(out, "compare_conditions.png"), dpi=150); plt.close(fig)


def stats_test(df, out):
    """条件間に差があるかの検定（Kruskal-Wallis 検定。人数が少なくても使えるノンパラメトリック検定）"""
    try:
        from scipy import stats
    except ImportError:
        print("（scipy が無いので検定は省略: pip install scipy）"); return
    conds = sorted(df["condition"].unique())
    if len(conds) < 2:
        return
    rows = []
    for c in ["escape_time_s", "path_length_m", "hesitation_count", "turnback_count", "stop_time_s",
              "sign_fixations", "time_in_smoke_s", "crouch_time_s", "final_dose"]:
        if c not in df.columns:
            continue
        groups = [df[df["condition"] == k][c].dropna().to_numpy() for k in conds]
        if any(len(g) < 2 for g in groups) or np.all(np.concatenate(groups) == np.concatenate(groups)[0]):
            continue
        h, p = stats.kruskal(*groups)
        rows.append(dict(metric=c, H=round(h, 3), p_value=round(p, 4), significant_5pct=p < 0.05,
                         **{f"median_{k}": round(float(np.median(g)), 2) for k, g in zip(conds, groups)}))
    if rows:
        pd.DataFrame(rows).to_csv(os.path.join(out, "stats_kruskal.csv"), index=False, encoding="utf-8-sig")
        print("\n検定結果（p<0.05 なら条件間に差がある可能性）:")
        print(pd.DataFrame(rows)[["metric", "H", "p_value", "significant_5pct"]].to_string(index=False))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("log_dir")
    ap.add_argument("--out", default="analysis_out")
    ap.add_argument("--floorplan", help="上から見た平面図の画像（任意）")
    ap.add_argument("--extent", nargs=4, type=float, metavar=("XMIN", "XMAX", "ZMIN", "ZMAX"),
                    help="平面図画像がワールド座標で占める範囲")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    sessions = load_sessions(a.log_dir)
    if not sessions:
        print("ログが見つかりません:", a.log_dir); return
    rows, all_stops = [], []
    for s in sessions:
        m, stops, turns = session_metrics(s)
        rows.append(m)
        if len(stops):
            st = stops.copy(); st.insert(0, "session", s["name"]); st.insert(1, "condition", s["condition"])
            all_stops.append(st)
        plot_session(s, stops, turns, a.out, a.floorplan, a.extent)
        print(f"{s['name']}: {m['escape_time_s']}s, 迷い{m['hesitation_count']}回, 引き返し{m['turnback_count']}回")
    df = pd.DataFrame(rows)
    df.to_csv(os.path.join(a.out, "summary.csv"), index=False, encoding="utf-8-sig")
    if all_stops:
        pd.concat(all_stops).to_csv(os.path.join(a.out, "stops.csv"), index=False, encoding="utf-8-sig")
    plot_overlay(sessions, a.out, a.floorplan, a.extent)
    stats_test(df, a.out)
    plot_compare(df, a.out)
    print("\n条件別平均:\n", df.groupby("condition")[["escape_time_s", "path_length_m", "hesitation_count", "turnback_count"]].mean().round(2))
    print("\n出力:", os.path.abspath(a.out))


if __name__ == "__main__":
    main()
