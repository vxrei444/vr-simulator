import pandas as pd

path = r"C:\Users\北澤泰樹\AppData\LocalLow\DefaultCompany\VRsimulator\EvacLogs\20261125_232128_P01_A_track.csv"
df = pd.read_csv(path,encoding="utf-8-sig")
print(df[["t","pos_x","pos_z"]])

import matplotlib.pyplot as plt

plt.rcParams["font.family"] = "Yu Gothic"
plt.plot(df["pos_x"], df["pos_z"], color="lightgray", linewidth=1)
sc = plt.scatter(df["pos_x"], df["pos_z"], c=df["t"], cmap="viridis", s=12)
plt.colorbar(sc, label="経過時間 [秒]")
plt.axis("equal")
plt.scatter(df["pos_x"].iloc[0], df["pos_z"].iloc[0], color="green", s=100, label="スタート")
plt.scatter(df["pos_x"].iloc[-1], df["pos_z"].iloc[-1], color="red", s=100, label="ゴール")
plt.xlabel("x [m]（東 →）")
plt.ylabel("z [m]（北 →）")
plt.title("移動経路（講義棟・P01・条件A・83.4秒)")
plt.legend()
import os
here = os.path.dirname(os.path.abspath(__file__))
plt.savefig(os.path.join(here, "taiki_kougitou_path.png"), dpi=200)
plt.show()