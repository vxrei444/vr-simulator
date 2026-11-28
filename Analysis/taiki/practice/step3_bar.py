import os
import pandas as pd
import matplotlib.pyplot as plt

plt.rcParams["font.family"] = "Yu Gothic"

path = r"C:\Users\北澤泰樹\AppData\LocalLow\DefaultCompany\VRsimulator\evacuation_results.csv"
df = pd.read_csv(path)
df = df[df["participant_id"] != "P00"]

mean = df.groupby("condition")["escape_time_sec"].mean()
print(mean)

plt.bar(mean.index, mean.values)
plt.ylabel("脱出時間 [秒]")
plt.title("条件ごとの平均脱出時間")

here = os.path.dirname(os.path.abspath(__file__))       # このファイルがあるフォルダ
plt.savefig(os.path.join(here, "step3_bar.png"), dpi=200, bbox_inches="tight")
plt.show()