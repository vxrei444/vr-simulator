import os
import pandas as pd
import matplotlib.pyplot as plt

plt.rcParams["font.family"] = "Yu Gothic"
here = os.path.dirname(os.path.abspath(__file__))

folder = os.path.join(os.path.expanduser("~"), "AppData", "LocalLow", "DefaultCompany", "VRsimulator", "EvacLogs")
path = os.path.join(folder, "20261125_232128_P01_A_track.csv")
df = pd.read_csv(path, encoding="utf-8-sig")

# 高さで階を決める
df["floor"] = pd.cut(df["pos_y"], bins=[-100, 12, 25, 100], labels=["1F", "2F", "3F"])
print(df["floor"].value_counts())

# 階ごとに縦に並べて描く
fig, axes = plt.subplots(3, 1, figsize=(12, 10))
for ax, f in zip(axes, ["3F", "2F", "1F"]):
    d = df[df["floor"] == f]
    img = plt.imread(os.path.join(here, f"map_{f}.png"))
    ax.imshow(img, extent=[-230, 70, -60, 60])
    ax.plot(d["pos_x"], d["pos_z"], color="red", linewidth=2)
    ax.set_title(f)
    ax.set_xlim(-230, 70)
    ax.set_ylim(-60, 60)

plt.suptitle("階ごとの移動経路（講義棟・P01・条件A・83.4秒）")
plt.tight_layout()
plt.savefig(os.path.join(here, "taiki_floors.png"), dpi=200)
plt.show()