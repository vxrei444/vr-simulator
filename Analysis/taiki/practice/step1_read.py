import pandas as pd

path = r"C:\Users\北澤泰樹\AppData\LocalLow\DefaultCompany\VRsimulator\evacuation_results.csv"
df = pd.read_csv(path)

print(df)
print("列の名前:",list(df.columns))
print("行の数:",len(df))