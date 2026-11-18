# taiki：避難ログの可視化（図・表）

## ファイル
| 場所 | 内容 |
|---|---|
| `Assets/taiki/Editor/taiki_LayoutExporter.cs` | Unity メニュー **Tools > taiki > 間取りを書き出す（分析用JSON）**。シーンの床・壁・階段・各階と、炎・障害物・スタート・ゴールの位置を JSON に出力 |
| `Assets/taiki/taiki_MapMarker.cs` | 炎・障害物・標識などに付ける目印（図に表示される）。`FireVisual` の炎は自動で拾う |
| `Analysis/taiki/taiki_visualize_position.py` | ログCSV＋間取りJSON → 図・表（PNG） |
| `Analysis/taiki/taiki_layout.py` | 間取りJSONの読み込み・最短経路・ゴールまでの道のりの計算（上から呼ばれる） |
| `Analysis/taiki/taiki_make_dummy_data.py` | 動作確認用ダミー（2階建て・炎・障害物、条件A〜D×5人）を作る。**実験データではない** |

## いちばん簡単な使い方
`taiki_make_figures.bat` をダブルクリック → `EvacLogs`・`Logs` のログを全部読んで `figures` フォルダに図を作り、フォルダを開く（初回は Python のインストールが必要）。

## 手順
1. **Unity**：分析したいシーンを開く → 炎や障害物のオブジェクトに `taiki_MapMarker` を付ける（任意）
   → **Tools > taiki > 間取りを書き出す** → 「書き出す」→ `layout_シーン名.json` を保存
   - 階の高さ範囲は空欄なら自動判定。うまく分かれないときは `1F:-1:3.5, 2F:3.5:7.5` のように指定
   - コライダーの無い物は壁にも床にもならない（見えない）ので注意
2. **プレイしてログを取る**（`taiki_PositionLogger` → `…/Logs/position_*.csv`、sho の `EvacuationTrajectoryLogger` → `…/EvacLogs/*_track.csv` も可）
3. **Python**
```bash
pip install pandas matplotlib numpy scipy
python taiki_visualize_position.py "<Logsフォルダ>" --layout layout_シーン名.json --out figures
# ダミーで試す
python taiki_make_dummy_data.py
python taiki_visualize_position.py sample_DUMMY/logs --layout sample_DUMMY/layout_dummy_2floors.json --out figures_DUMMY
```

### 実験場（講義棟）の場合
講義棟は約3.5倍の縮尺（1単位 ≒ 0.29m）なので：
- 書き出し画面：格子 1.0 / 人の高さ 5 / 人の半径 0.7 / 越えられる段差 1.5（目安）。
  「調べる範囲を手動で指定」→ Hierarchy で `building-left`・`buildings-right`・`Outside_Ground`（出口の外）を選択 →「選択中のオブジェクトの範囲を使う」
  （遠くにある談話室 `danwasitu` などを範囲に入れないため）
- Python：`--scale 0.29 --height-offset 1.2` を付ける（距離・速度が m で出る）。正確な縮尺はショウに確認

## 出力される図
| ファイル | 内容 |
|---|---|
| `A1_trajectory_*.png` | 1プレイの移動経路を**階ごと**に（色＝経過時間、点線＝最短経路、炎・障害物・出口つき） |
| `A2_trajectory_all.png` | 全プレイを条件別の色で重ね描き（階ごと） |
| `B1_heatmap_by_condition.png` | 通った場所のヒートマップ（条件×階、1人平均、全条件で同じ色の尺度） |
| `C1_route_compare_*.png` | 最短経路と実際の経路の重ね描き（外れた区間を赤）＋最短経路からのズレの時間変化 |
| `C2_route_excess_by_condition.png` | 条件別：余分に歩いた距離・迂回率・最短経路から外れていた時間 |
| `D1_distance_to_goal_timeseries.png` | 横軸 時間／縦軸 ゴールまでの距離（**壁を通らない道のり**）。条件ごとに協力者の平均±標準誤差 |
| `E1_summary_table.png` / `summary.csv` | プレイごとの指標と条件別平均 |

## 計算の決まり
- **最短経路・ゴールまでの距離**：間取りJSONの「歩ける場所」をつないだ網の上で計算（壁は通らない、階段は段差 0.45m 以下でつながる）
- **迂回率**＝実際の移動距離 ÷ 最短距離（1.0＝最短）
- **外れた**＝最短経路から 1.5m 以上離れている（別の階にいるときも外れている扱い）
- **立ち止まり**＝速度 0.2m/s 未満
- ログの y（頭の高さ）から 1.2m 引いた高さに一番近い床を「今いる場所」とみなす（`--height-offset` で変更可）
- 時系列は 0.5 秒刻みにそろえ、記録終了後はゴール到達なら 0m として平均する
