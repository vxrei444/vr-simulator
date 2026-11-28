import os
import numpy as np
import pandas as pd
import matplotlib.pyplot as plt

plt.rcParams["font.family"] = "Yu Gothic"
here = os.path.dirname(os.path.abspath(__file__))

folder = os.path.join(os.path.expanduser("~"), "AppData", "LocalLow", "DefaultCompany", "VRsimulator", "EvacLogs")
path = os.path.join(folder, "20261125_232128_P01_A_track.csv")
df = pd.read_csv(path, encoding="utf-8-sig")

# 高さで階を決める
df["floor"] = pd.cut(df["pos_y"], bins=[-100, 12, 25, 100], labels=["1F", "2F", "3F"])

# 推奨経路（階ごとの経由点 (x, z)）※仮の値。見取り図から読み取った値に書き換える
# 推奨経路：3Fを西へ → 西の階段で1Fへ → 出口
recommended = {
    "3F": [(12, 27), (5, 22), (-195, 22), (-205, 10)],
    "2F": [(-205, 10), (-215, 3), (-205, -2)],
    "1F": [(-205, -2), (-200, -15), (-169, -17), (-169, -28)],
}

# 炎の位置（見取り図に写らないので印を打つ）
fires = {"1F": [(-83.6, 21.4)]}

# 距離を計算する
actual_len = np.hypot(df["pos_x"].diff(), df["pos_z"].diff()).sum()
rec_len = 0
for f, pts in recommended.items():
    p = np.array(pts)
    rec_len += np.hypot(np.diff(p[:, 0]), np.diff(p[:, 1])).sum()
ratio = actual_len / rec_len
print(f"実際の移動距離：{actual_len:.1f}")
print(f"推奨経路の距離：{rec_len:.1f}")
print(f"迂回率（実際÷推奨）：{ratio:.2f}")

# 階ごとに縦に並べて描く
fig, axes = plt.subplots(3, 1, figsize=(10, 11))
for ax, f in zip(axes, ["3F", "2F", "1F"]):
    d = df[df["floor"] == f]
    img = plt.imread(os.path.join(here, "maps_A",f"map_{f}.png"))
    ax.imshow(img, extent=[-230, 70, -60, 60])
    p = np.array(recommended[f])
    ax.plot(p[:, 0], p[:, 1], color="lime", linewidth=3, linestyle="--", label="推奨経路")
    ax.plot(d["pos_x"], d["pos_z"], color="red", linewidth=2, label="実際の経路")
    for x, z in fires.get(f, []):
        ax.scatter(x, z, marker="^", color="orangered", s=250, edgecolor="black", zorder=5, label="炎")
    ax.set_title(f)
    ax.set_xlim(-230, 70)
    ax.set_ylim(-60, 60)
handles, labels = axes[2].get_legend_handles_labels()
axes[1].legend(handles, labels, loc="center left", bbox_to_anchor=(1.02, 0.5), fontsize=12)
plt.suptitle(f"推奨経路と実際の経路（講義棟・P01・条件A）　実際 {actual_len:.0f} ／ 推奨 {rec_len:.0f}（迂回率 {ratio:.2f}）")
plt.tight_layout(rect=[0,0,1,0.96])
plt.savefig(os.path.join(here, "taiki_recommended.png"), dpi=200)
plt.show()