# testScene（対照実験用シーン）の使い方

## 入れ方（1回だけ）
1. Unity を閉じる（開いたままでも可だが、閉じた方が安全）
2. この zip の中の `Assets` フォルダを、プロジェクト（`Documents\GitHub\vr-simulator`）の `Assets` に**上書きコピー**
   - 追加される: `Assets/sho/Experiment/`（Runtime 7本・Editor 1本）
   - 置き換わる: `Assets/sho/Script/EvacuationTrajectoryLogger.cs`（ログに煙の列を足せるようにした版）
3. Unity を開き、Console に赤いエラーが無いことを確認
4. メニュー **Tools > 避難実験 > testScene を作成** → `Assets/sho/testScene.unity` ができる

## 遊び方（エディタ）
- ▶ 再生 → WASD 移動 / マウス視点 / Shift 速歩き / **C しゃがむ**
- スタート前に **1〜5** で誘導条件、**Q**=煙なし **E**=標準（Alt+W 遅い / Alt+R 速い）
- 画面左上に 条件・煙層の高さ・減光係数・見通し距離 が出る
- 出口（東端）に入るとゴール。煙を吸いすぎると「行動不能」で終了

## 条件
| 誘導 | 中身 |
|---|---|
| A-None | サインなし（基準） |
| B-FloorArrow | 床の矢印（常時点灯） |
| C-GlowSign | 光る看板（5か所・常時点灯） |
| D-Flashing | C と**同じ位置・同じ看板**で点滅だけ違う（1Hz） |
| E-FlowingArrow | 床の光が出口へ流れる |

煙: Off / Slow / Medium / Fast（t²火源の成長率 α = 0.00293 / 0.0117 / 0.0469 kW/s²）

## Inspector で変えられる主な値
- **FlashingBeacon**（看板・矢印ごと）: パターン（Steady/Blink/Pulse/Strobe/DoubleFlash）、速さ Hz、点灯割合、色、明るさ、消灯時の明るさ
  - 光過敏性発作への配慮で 3Hz を超えないよう制限（allowOver3Hz で解除可、非推奨）
- **FlowingArrowLights**: 流れる速さ m/s、帯の長さ、間隔、色、明るさ（配置を変えたら ⋮ → Rebuild Segments）
- **SmokeLayerController**: モード（FirePlume 物理計算 / Linear 直線降下）、成長率、最大発熱、事前燃焼時間、すす・CO 発生率、霧の倍率
- **SmokeExposure**（Player）: 刺激が出る K、段階のしきい値、行動不能 FED/曝露量、速度低下、ビネット

## ログ（track.csv に追加される列）
`smoke_layer_m, k_head, co_head_ppm, in_smoke, irritation, fed, dose, stage, head_above_floor_m`
events.csv: `config_guidance, config_smoke, smoke_enter/exit, symptom_stage, cough, incapacitated`

## 解析
```
pip install pandas numpy matplotlib scipy
python analyze_evac_logs.py ログフォルダ --out 出力フォルダ
```
summary.csv に 煙の中にいた時間・しゃがんだ時間・最終FED・最大症状段階 が追加。
stats_kruskal.csv に条件間の検定（Kruskal-Wallis）。

## VR（Quest）で使うとき
testScene の Player は PC 操作用。Quest で使うときは 実験場 の VR プレイヤーに置き換え、
- Logger(EvacSession) の Head/Body、SmokeExposure の Head、SmokeLayerController の Viewer を CenterEyeAnchor に付け替え
- SymptomVignette / SymptomBlackout を CenterEyeAnchor の子に移す
- SmokeExposure を VR プレイヤー側に付け直す（movements は空でよい）
