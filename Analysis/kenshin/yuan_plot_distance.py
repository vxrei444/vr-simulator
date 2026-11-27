import argparse
import colorsys
import glob
import hashlib
import os

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
import pandas as pd

# ---------- 設定 ----------
GOAL_X, GOAL_Z = -168.7, -23.7      # 屋外出口の位置
GOAL_THRESHOLD = 10.0               # 最後の dist がこれ未満ならゴール到達とみなす(単位)
TIME_LIMIT = 300.0                  # 制限時間 [秒]
DT = 0.5                            # 共通時刻の刻み [秒]
EXCLUDE_IDS = {"P00"}               # 動作確認用ID
DEFAULT_FOLDER = os.path.join(
    os.path.expanduser("~"), "AppData", "LocalLow",
    "DefaultCompany", "VRsimulator", "EvacLogs",
)
# 条件名 -> 色。ここに書いた条件は常に同じ色になる。未登録の条件は名前から自動で決まる。
CONDITION_COLORS = {
    "A": "#7f7f7f",
    "B": "#1f77b4",
    "C": "#2ca02c",
    "D": "#d62728",
}


def yuan_setup_font():
    """日本語フォント(Yu Gothic)を設定。無ければ他の日本語フォントにフォールバック。"""
    plt.rcParams["font.family"] = "sans-serif"
    plt.rcParams["font.sans-serif"] = [
        "Yu Gothic", "Meiryo", "MS Gothic", "Hiragino Sans", "Noto Sans CJK JP",
        "IPAexGothic", "DejaVu Sans",
    ]
    plt.rcParams["axes.unicode_minus"] = False


def yuan_color_for(cond):
    """条件名から色を固定で決める(条件数が変わっても入れ替わらない)。"""
    if cond in CONDITION_COLORS:
        return CONDITION_COLORS[cond]
    h = int(hashlib.md5(cond.encode("utf-8")).hexdigest(), 16)
    hue = (h % 360) / 360.0
    r, g, b = colorsys.hsv_to_rgb(hue, 0.75, 0.80)
    return (r, g, b)


def yuan_parse_filename(path):
    """ファイル名から (協力者ID, 条件名) を取り出す。形式に合わなければ None。"""
    stem = os.path.splitext(os.path.basename(path))[0]   # ..._track
    parts = stem.split("_")
    if len(parts) < 5 or parts[-1] != "track":
        return None
    pid = parts[2]
    cond = "_".join(parts[3:-1])
    return pid, cond


def yuan_load_tracks(folder):
    """*_track.csv を全部読み、ファイルごとの辞書のリストを返す。"""
    paths = sorted(glob.glob(os.path.join(folder, "*_track.csv")))
    if not paths:
        raise SystemExit(f"CSVが見つかりません: {folder}")
    records = []
    for path in paths:
        parsed = yuan_parse_filename(path)
        if parsed is None:
            print(f"[skip] ファイル名の形式が違います: {os.path.basename(path)}")
            continue
        pid, cond = parsed
        if pid in EXCLUDE_IDS:
            continue
        try:
            df = pd.read_csv(path, encoding="utf-8-sig")
            df = df[["t", "pos_x", "pos_y", "pos_z"]].dropna().sort_values("t")
        except Exception as e:
            print(f"[skip] 読み込み失敗 {os.path.basename(path)}: {e}")
            continue
        if len(df) < 2:
            print(f"[skip] 行数不足: {os.path.basename(path)}")
            continue
        df = yuan_add_dist(df)
        records.append({
            "pid": pid, "cond": cond, "file": os.path.basename(path),
            "t": df["t"].to_numpy(float), "dist": df["dist"].to_numpy(float),
        })
    if not records:
        raise SystemExit("使えるデータがありません。")
    return records


def yuan_add_dist(df):
    """ゴールまでの平面直線距離の列 dist を追加する(あとで歩行距離に差し替える場所)。"""
    df = df.copy()
    df["dist"] = np.sqrt((df["pos_x"] - GOAL_X) ** 2 + (df["pos_z"] - GOAL_Z) ** 2)
    return df


def yuan_resample(records):
    """共通時刻(0, 0.5, ...)に補間してそろえる。記録終了後は 0 か最後の距離。"""
    t_max = max(r["t"][-1] for r in records)
    grid = np.arange(0, t_max + DT / 2, DT)
    for r in records:
        t, d = r["t"], r["dist"]
        y = np.interp(grid, t, d)
        goaled = d[-1] < GOAL_THRESHOLD
        after = grid > t[-1]
        y[after] = 0.0 if goaled else d[-1]
        r["y"] = y
        r["goaled"] = goaled
        r["t_end"] = t[-1]
    return grid


def yuan_summarize(records):
    """条件ごとの平均・標準誤差・人数を計算。"""
    conds = sorted({r["cond"] for r in records})
    stats = {}
    for c in conds:
        ys = np.array([r["y"] for r in records if r["cond"] == c])
        n = ys.shape[0]
        mean = ys.mean(axis=0)
        se = ys.std(axis=0, ddof=1) / np.sqrt(n) if n > 1 else None
        stats[c] = {"n": n, "mean": mean, "se": se}
    return stats


def yuan_plot(records, grid, stats, out_path):
    yuan_setup_font()
    fig, ax = plt.subplots(figsize=(10, 6))
    for c, s in stats.items():
        color = yuan_color_for(c)
        for r in records:
            if r["cond"] == c:
                ax.plot(grid, r["y"], color=color, lw=0.8, alpha=0.3)
        if s["se"] is not None:
            ax.fill_between(grid, s["mean"] - s["se"], s["mean"] + s["se"],
                            color=color, alpha=0.2, linewidth=0)
        ax.plot(grid, s["mean"], color=color, lw=3, label=f"{c} (n={s['n']})")
    ax.set_xlabel("経過時間 [秒]")
    ax.set_ylabel("ゴールまでの直線距離 [Unity単位]")
    ax.set_title("条件ごとのゴールまでの距離の推移(直線距離・平均±標準誤差)")
    ax.set_xlim(0, grid[-1])
    ax.set_ylim(bottom=0)
    ax.grid(alpha=0.3)
    ax.legend(title="条件", loc="best")
    fig.text(0.01, 0.01,
             "注: 水平面の直線距離。3Fから東の階段へ向かう間は一時的に増える(建物構造のため)。"
             "1単位は約0.29m",
             fontsize=8, color="gray")
    fig.tight_layout(rect=(0, 0.03, 1, 1))
    fig.savefig(out_path, dpi=200)
    plt.close(fig)


def yuan_print_table(records, stats):
    print(f"{'条件':<28}{'人数':>5}{'平均脱出時間[秒]':>18}{'ゴール到達':>12}")
    for c in stats:
        rs = [r for r in records if r["cond"] == c]
        mean_t = np.mean([r["t_end"] for r in rs])
        n_goal = sum(r["goaled"] for r in rs)
        print(f"{c:<28}{len(rs):>5}{mean_t:>18.1f}{n_goal:>8}/{len(rs)}")


def yuan_default_out():
    """出力先: スクリプトと同じフォルダ。Jupyterなど __file__ が無い場合はカレントフォルダ。"""
    try:
        base = os.path.dirname(os.path.abspath(__file__))
    except NameError:
        base = os.getcwd()
    return os.path.join(base, "distance_timeseries.png")


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("--folder", default=DEFAULT_FOLDER)
    ap.add_argument("--out", default=yuan_default_out())
    # Jupyterが渡す余計な引数(-f ...)を無視するため parse_known_args を使う
    args, _ = ap.parse_known_args(argv)

    records = yuan_load_tracks(args.folder)
    grid = yuan_resample(records)
    stats = yuan_summarize(records)
    yuan_plot(records, grid, stats, args.out)
    yuan_print_table(records, stats)
    print(f"保存しました: {args.out}")


if __name__ == "__main__":
    main()
