import pandas as pd
import matplotlib.pyplot as plt

plt.rcParams["font.family"] = "Yu Gothic"

path = r"C:\Users\北澤泰樹\AppData\LocalLow\DefaultCompany\VRsimulator\EvacLogs\20261125_232128_P01_A_track.csv"
df = pd.read_csv(path, encoding="utf-8-sig")

print(df["pos_y"].describe())

plt.plot(df["t"], df["pos_y"])
plt.xlabel("経過時間 [秒]")
plt.ylabel("高さ pos_y")
plt.title("高さの変化（講義棟）")
plt.show()