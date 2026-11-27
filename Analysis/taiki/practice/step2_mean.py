import pandas as pd

path = r"C:\Users\北澤泰樹\AppData\LocalLow\DefaultCompany\VRsimulator\evacuation_results.csv"
df = pd.read_csv(path)

# P00（動作確認のプレイ）を除く
df = df[df["participant_id"] != "P00"]
print(df)

# 条件ごとの平均・人数
summary = df.groupby("condition")["escape_time_sec"].agg(["mean", "count"])
print(summary)