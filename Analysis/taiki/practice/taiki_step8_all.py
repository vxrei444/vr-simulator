import os
import glob
import numpy as np
import pandas as pd

here = os.path.dirname(os.path.abspath(__file__))
folder = os.path.join(os.path.expanduser("~"), "AppData", "LocalLow", "DefaultCompany", "VRsimulator", "EvacLogs")

# 推奨経路（step7 と同じもの）
recommended = {
    "3F": [(12, 27), (5, 22), (-195, 22), (-205, 10)],
    "2F": [(-205, 10), (-215, 3), (-205, -2)],
    "1F": [(-205, -2), (-200, -15), (-169, -17), (-169, -28)],
}

# 推奨経路の距離（全員共通なので最初に1回だけ計算）
rec_len = 0
for f, pts in recommended.items():
    p = np.array(pts)
    rec_len += np.hypot(np.diff(p[:, 0]), np.diff(p[:, 1])).sum()

# フォルダの中の _track.csv を全部さがす
files = sorted(glob.glob(os.path.join(folder, "*_track.csv")))
print(f"見つかったファイル：{len(files)}個")

rows = []
for path in files:
    # ファイル名から協力者と条件を取り出す
    # 例：20261125_232128_P01_A_track.csv → ["20261125", "232128", "P01", "A", "track.csv"]
    name = os.path.basename(path)
    parts = name.split("_")
    participant = parts[2]
    condition = parts[3]

    df = pd.read_csv(path, encoding="utf-8-sig")
    actual_len = np.hypot(df["pos_x"].diff(), df["pos_z"].diff()).sum()
    time = df["t"].iloc[-1]

    rows.append({
        "ファイル": name,
        "協力者": participant,
        "条件": condition,
        "時間(秒)": round(time, 1),
        "実際の距離": round(actual_len, 1),
        "迂回率": round(actual_len / rec_len, 2),
    })

# 1人1行の表にする
result = pd.DataFrame(rows)
print(result)

# 条件ごとの平均
summary = result.groupby("条件")[["時間(秒)", "実際の距離", "迂回率"]].mean().round(2)
summary["人数"] = result.groupby("条件").size()
print(summary)

# Excel で開ける CSV に保存
result.to_csv(os.path.join(here, "taiki_all_results.csv"), index=False, encoding="utf-8-sig")
summary.to_csv(os.path.join(here, "taiki_condition_summary.csv"), encoding="utf-8-sig")