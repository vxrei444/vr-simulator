using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 天井にたまった煙（煙層）が時間とともに下がってくる様子を計算・表示する。
///
/// 【計算モデル】二層ゾーンモデル（上：高温の煙層 / 下：きれいな空気）
///   ・火災の大きさ  Q(t) = α t²（t²火源。α は 遅い/標準/速い/超速 の成長率）
///   ・煙層に流れ込む煙の量 … NFPA 92 の軸対称プルーム式
///       m_p = 0.071 Qc^(1/3) z^(5/3) + 0.0018 Qc   （z: 火源から煙層下端までの高さ）
///   ・煙層の温度 … 煙層にたまった熱（壁への熱損失 λ を差し引き）から計算
///   ・煙層の厚さ  = 煙の体積 / 床面積  →  煙層下端の高さ = 天井高 − 厚さ
///   ・煙の濃さ（減光係数 K）= Km × すす量 / 煙層体積   （Km = 8700 m²/kg, Mulholland）
///   ・見通し距離  S = C / K  （C = 8: 光る看板, 3: 光らない看板, 神 忠久）
///   ・CO 濃度 … CO 発生率 × 燃えた量 / 煙層体積 （症状スクリプトの FED 計算に使う）
/// 開口部からの排煙・漏れは無視している（＝煙が逃げない、厳しめの想定）。
///
/// 【表示】regions（廊下などの長方形）ごとに黒い半透明の箱を天井〜煙層下端に表示し、
///         カメラの頭の位置の煙の濃さに合わせて Fog（霧）の濃さを毎フレーム変える。
///
/// Linear モードにすると、物理計算をせず「◯秒で◯mまで下がる」と直接指定できる（条件を揃えやすい）。
/// FloorByFloor モードは複数階の建物用：最上階の天井から煙がたまり始め、上の階がある程度埋まると
/// 階段などを伝って1つ下の階の天井にも煙がたまり始める…と、上の階から順に（少しずつ重なりながら）充満する。
/// 各階の煙は「最初は速く、床に近づくほどゆっくり」下がり、入り始めは薄く、だんだん濃くなる。
///
/// 高さ（floorY / ceilingHeight / floors）はワールド座標の単位。
/// minLayerHeight・interfaceBlend・減光係数は実寸（m）。建物を拡大して置いている場合は
/// worldUnitsPerMeter（1m が何ユニットか）を設定すると、霧の見え方と計算が実寸に合う。
/// </summary>
public class SmokeLayerController : MonoBehaviour
{
    public enum Mode { FirePlume, Linear, FloorByFloor }
    public enum Growth { Slow, Medium, Fast, UltraFast }

    [Serializable]
    public class Region
    {
        public string name = "Corridor";
        [Tooltip("床面の中心（ワールド座標 XZ）")]
        public Vector2 centerXZ;
        [Tooltip("床面の大きさ X(幅) × Z(奥行) [m]")]
        public Vector2 sizeXZ = new Vector2(2.5f, 10f);
        [HideInInspector] public Transform volume;
    }

    [Serializable]
    public class FloorLevel
    {
        public string name = "3F";
        [Tooltip("この階の床の高さ（ワールド Y）")]
        public float floorY;
        [Tooltip("この階の天井の高さ（ワールド Y）")]
        public float ceilingY;
        [Tooltip("この階が天井から床近くまで煙で埋まるのにかかる秒数")]
        public float fillSeconds = 60f;
    }

    [Header("モード")]
    public Mode mode = Mode.FirePlume;
    [Tooltip("煙を出すかどうか（煙なし条件では false）")]
    public bool smokeEnabled = true;

    [Header("単位")]
    [Tooltip("ワールドの 1m が何ユニットか（建物やプレイヤーを拡大している場合に設定。等倍なら 1）")]
    public float worldUnitsPerMeter = 1f;
    [Tooltip("EvacuationSessionManager が無い／スタート前でも、シーン開始から煙を進める")]
    public bool runWithoutSession = false;

    [Header("空間")]
    [Tooltip("床の高さ（ワールド Y）")]
    public float floorY = 0f;
    [Tooltip("天井の高さ（床から, m）")]
    public float ceilingHeight = 2.7f;
    [Tooltip("煙層が下がる下限（床から, m）")]
    public float minLayerHeight = 0.3f;
    [Tooltip("煙がたまる範囲（廊下ごとの長方形）。床面積はこの合計から計算される")]
    public List<Region> regions = new List<Region>();

    [Header("火災（FirePlume モード）")]
    public Growth growth = Growth.Medium;
    [Tooltip("最大発熱速度 [kW]")]
    public float maxHRR = 3000f;
    [Tooltip("参加者がスタートした時点で、火災が始まってから何秒たっているか")]
    public float preburnSeconds = 30f;
    [Tooltip("発熱のうち対流で煙に運ばれる割合（一般に 0.7）")]
    [Range(0.4f, 1f)] public float convectiveFraction = 0.7f;
    [Tooltip("煙層の熱が壁・天井に逃げる割合（0.6〜0.9 程度）")]
    [Range(0f, 0.95f)] public float heatLossFraction = 0.7f;
    [Tooltip("燃焼熱 [kJ/kg]（木材・家具 15000〜25000）")]
    public float heatOfCombustion = 20000f;
    [Tooltip("すすの発生率 [kg/kg]（木材 0.015、家具・プラスチック混合 0.05、ポリウレタン 0.1）")]
    public float sootYield = 0.05f;
    [Tooltip("CO の発生率 [kg/kg]（換気の良い火災 0.004〜0.04、換気不足 0.2 程度）")]
    public float coYield = 0.04f;
    [Tooltip("下の層（きれいな空気側）に混ざる煙の割合（0 = 混ざらない）")]
    [Range(0f, 0.3f)] public float lowerLayerLeak = 0.03f;

    [Header("階ごとに充満（FloorByFloor モード）")]
    [Tooltip("各階の床・天井の高さ。上の階から順に煙が充満する（並び順は自動で上の階から）")]
    public List<FloorLevel> floors = new List<FloorLevel>();
    [Tooltip("上の階がこの割合まで埋まったら、下の階にも煙が入り始める（1 = 完全に埋まってから）")]
    [Range(0f, 1f)] public float nextFloorStartFraction = 0.5f;
    [Tooltip("さらに追加で待つ秒数（階段を煙が伝ってくる時間）")]
    public float floorGapSeconds = 0f;
    [Tooltip("各階での煙の下がり方（横: 経過の割合 0〜1, 縦: 埋まった割合 0〜1）。初期値は最初速く、だんだんゆっくり")]
    public AnimationCurve fillCurve = new AnimationCurve(new Keyframe(0f, 0f, 1.8f, 1.8f), new Keyframe(1f, 1f, 0.25f, 0.25f));
    [Tooltip("煙が入り始めた直後の濃さ（最大の何割か）。埋まるにつれて最大の濃さになる")]
    [Range(0f, 1f)] public float initialDensity = 0.35f;
    [Tooltip("計測が終わった（ゴール・時間切れ・行動不能）後も煙を進め続ける")]
    public bool continueAfterSessionEnd = true;

    [Header("直線降下（Linear / FloorByFloor モード共通）")]
    [Tooltip("スタートから煙が下がり始めるまでの秒数")]
    public float linearDelay = 0f;
    [Tooltip("天井から下限まで下がるのにかかる秒数")]
    public float linearDuration = 180f;
    [Tooltip("Linear モードの煙層の減光係数 [1/m]")]
    public float linearExtinction = 2f;
    [Tooltip("Linear モードの CO 濃度 [ppm]")]
    public float linearCOppm = 300f;

    [Header("見た目")]
    [Tooltip("煙層の箱に使う材質（黒い半透明）")]
    public Material smokeVolumeMaterial;
    public Color smokeColor = new Color(0.05f, 0.05f, 0.05f, 1f);
    [Tooltip("霧（Fog）の濃さの倍率。見た目が薄すぎる/濃すぎるときに調整")]
    [Range(0.1f, 3f)] public float fogScale = 1f;
    [Tooltip("煙層の境目のぼかし幅 [m]")]
    public float interfaceBlend = 0.15f;
    [Tooltip("煙の中にいないときの霧の濃さ（遠くの見え方）")]
    public float baseFogDensity = 0.01f;
    [Tooltip("霧の濃さを計算する基準カメラ（未設定なら Main Camera）")]
    public Transform viewer;
    [Tooltip("煙層の下端に浮かぶ煙のパーティクル（任意）")]
    public ParticleSystem interfaceParticles;

    // ---- 出力（他スクリプト・ログ用） ----
    public float FireTime { get; private set; }
    public float HRR { get; private set; }
    public float LayerHeightAboveFloor { get; private set; }
    public float LayerY => floorY + LayerHeightAboveFloor;
    public float ExtinctionUpper { get; private set; }
    public float VisibilityUpper => ExtinctionUpper > 1e-4f ? 8f / ExtinctionUpper : 999f;
    public float SmokeTempC { get; private set; } = 20f;
    public float COppmUpper { get; private set; }
    public float FloorArea { get; private set; }
    /// <summary>今、煙が下がってきている階の名前（FloorByFloor モード）</summary>
    public string CurrentFloorName { get; private set; } = "";
    float Upm => Mathf.Max(0.01f, worldUnitsPerMeter);

    // ---- 内部状態（ゾーンモデル） ----
    const float Ta = 293.15f, Cp = 1.0f, KmSoot = 8700f, RhoCO = 1.145f, SimDt = 0.1f;
    float simT, layerMass, layerHeat, fuelMass;
    float sessionEndRealTime = -1f;

    // ---- FloorByFloor の各階の状態（上の階から順） ----
    readonly List<FloorLevel> sorted = new List<FloorLevel>();
    float[] fLayerY = new float[0], fK = new float[0], fCO = new float[0];
    bool[] fStarted = new bool[0];
    readonly Dictionary<string, Transform> floorVolumes = new Dictionary<string, Transform>();

    void Awake()
    {
        if (viewer == null && Camera.main != null) viewer = Camera.main.transform;
        EnsureVolumes();
        ResetSim();
        AdvanceTo(preburnSeconds);
        Refresh();
    }

    void ComputeArea()
    {
        float a = 0f;
        if (regions != null) foreach (var r in regions) a += Mathf.Max(0.01f, r.sizeXZ.x * r.sizeXZ.y);
        FloorArea = Mathf.Max(1f, a);
    }

    public void ResetSim()
    {
        ComputeArea();
        if (mode == Mode.FloorByFloor && floors != null && floors.Count > 0)
        {
            // 一番下の階の床〜一番上の階の天井 を「空間」として使う
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var f in floors) { lo = Mathf.Min(lo, f.floorY); hi = Mathf.Max(hi, f.ceilingY); }
            floorY = lo; ceilingHeight = hi - lo;
        }
        PrepareFloors();
        simT = 0f; layerMass = 1e-4f; layerHeat = 0f; fuelMass = 0f;
        LayerHeightAboveFloor = ceilingHeight; ExtinctionUpper = 0f; COppmUpper = 0f; HRR = 0f;
    }

    /// <summary>条件プリセット（ExperimentConditionManager から呼ばれる）</summary>
    public void ApplyPreset(ExperimentConditionManager.SmokePreset p)
    {
        smokeEnabled = p != ExperimentConditionManager.SmokePreset.Off;
        switch (p)
        {
            case ExperimentConditionManager.SmokePreset.Slow: growth = Growth.Slow; break;
            case ExperimentConditionManager.SmokePreset.Medium: growth = Growth.Medium; break;
            case ExperimentConditionManager.SmokePreset.Fast: growth = Growth.Fast; break;
        }
        ResetSim();
        AdvanceTo(preburnSeconds);
        Refresh();
    }

    public static float Alpha(Growth g)
    {
        switch (g)
        {
            case Growth.Slow: return 0.00293f;     // kW/s²（600秒で 1MW）
            case Growth.Medium: return 0.01172f;   // 300秒で 1MW
            case Growth.Fast: return 0.0469f;      // 150秒で 1MW
            default: return 0.1876f;               // 75秒で 1MW
        }
    }

    void Update()
    {
        var m = EvacuationSessionManager.Instance;
        float sessionT = 0f;
        if (m != null && m.State == EvacuationState.Running)
        {
            sessionT = m.ElapsedTime;
            sessionEndRealTime = -1f;
        }
        else if (m != null && m.State != EvacuationState.Waiting)
        {
            // 計測終了後：continueAfterSessionEnd なら煙は進み続ける
            if (sessionEndRealTime < 0f) sessionEndRealTime = Time.time;
            sessionT = m.ElapsedTime + (continueAfterSessionEnd ? Time.time - sessionEndRealTime : 0f);
        }
        else if (runWithoutSession) sessionT = Time.timeSinceLevelLoad;
        float target = preburnSeconds + sessionT;
        if (target > simT) AdvanceTo(target);
        Refresh();
    }

    void AdvanceTo(float t)
    {
        if (!smokeEnabled)
        {
            simT = t; FireTime = t; HRR = 0f; LayerHeightAboveFloor = ceilingHeight; ExtinctionUpper = 0f; COppmUpper = 0f;
            return;
        }
        if (mode == Mode.FloorByFloor)
        {
            simT = t; FireTime = t; HRR = 0f;
            AdvanceFloorByFloor(t - preburnSeconds - linearDelay);
            return;
        }
        if (mode == Mode.Linear)
        {
            simT = t; FireTime = t;
            float u = Mathf.Clamp01((t - preburnSeconds - linearDelay) / Mathf.Max(1f, linearDuration));
            if (t - preburnSeconds < 0f) u = 0f;
            LayerHeightAboveFloor = Mathf.Lerp(ceilingHeight, minLayerHeight * Upm, u);
            ExtinctionUpper = u > 0f ? linearExtinction : 0f;
            COppmUpper = u > 0f ? linearCOppm : 0f;
            HRR = 0f;
            return;
        }

        float alpha = Alpha(growth);
        float upm = Upm;
        float areaM = FloorArea / (upm * upm);   // 実寸の床面積 [m²]
        float ceilM = ceilingHeight / upm;       // 実寸の天井高 [m]
        while (simT < t)
        {
            float dt = Mathf.Min(SimDt, t - simT);
            float Q = Mathf.Min(alpha * simT * simT, maxHRR);
            float Qc = convectiveFraction * Q;
            float z = LayerHeightAboveFloor / upm; // 実寸[m]。火源は床面にあると仮定
            float mp = 0f;
            if (Qc > 0f && z > minLayerHeight + 0.01f)
            {
                float zl = 0.166f * Mathf.Pow(Qc, 0.4f); // 平均火炎高さ
                mp = z > zl
                    ? 0.071f * Mathf.Pow(Qc, 1f / 3f) * Mathf.Pow(z, 5f / 3f) + 0.0018f * Qc
                    : 0.032f * Mathf.Pow(Qc, 0.6f) * z;
            }
            layerMass += mp * dt;
            layerHeat += (1f - heatLossFraction) * Qc * dt;
            fuelMass += Q / heatOfCombustion * dt;

            float Ts = Ta + layerHeat / (Cp * layerMass);
            float rho = 353f / Ts;
            float V = layerMass / rho;
            float zNew = Mathf.Max(minLayerHeight, ceilM - V / areaM);
            LayerHeightAboveFloor = zNew * upm;
            float Vlayer = Mathf.Max(0.5f, (ceilM - zNew) * areaM);
            ExtinctionUpper = KmSoot * sootYield * fuelMass / Mathf.Max(V, Vlayer);
            COppmUpper = coYield * fuelMass / Mathf.Max(V, Vlayer) / RhoCO * 1e6f;
            SmokeTempC = Ts - 273.15f;
            HRR = Q;
            simT += dt;
        }
        FireTime = simT;
    }

    void PrepareFloors()
    {
        sorted.Clear();
        if (floors != null) sorted.AddRange(floors);
        sorted.Sort((a, b) => b.floorY.CompareTo(a.floorY)); // 上の階から
        int n = sorted.Count;
        if (fLayerY.Length != n) { fLayerY = new float[n]; fK = new float[n]; fCO = new float[n]; fStarted = new bool[n]; }
        for (int i = 0; i < n; i++) { fLayerY[i] = sorted[i].ceilingY; fK[i] = 0f; fCO[i] = 0f; fStarted[i] = false; }
    }

    void AdvanceFloorByFloor(float tt)
    {
        if (sorted.Count != (floors != null ? floors.Count : 0)) PrepareFloors();
        float minU = minLayerHeight * Upm;
        float start = 0f;
        for (int i = 0; i < sorted.Count; i++)
        {
            var f = sorted[i];
            float dur = Mathf.Max(1f, f.fillSeconds);
            float u = (tt - start) / dur;
            if (tt < start || tt < 0f)
            {
                fStarted[i] = false; fLayerY[i] = f.ceilingY; fK[i] = 0f; fCO[i] = 0f;
            }
            else
            {
                float prog = Mathf.Clamp01(fillCurve != null && fillCurve.length > 0 ? fillCurve.Evaluate(Mathf.Clamp01(u)) : Mathf.Clamp01(u));
                fStarted[i] = true;
                fLayerY[i] = Mathf.Lerp(f.ceilingY, Mathf.Min(f.ceilingY, f.floorY + minU), prog);
                float dens = Mathf.Lerp(initialDensity, 1f, prog);
                fK[i] = linearExtinction * dens;
                fCO[i] = linearCOppm * dens;
            }
            start += nextFloorStartFraction * dur + floorGapSeconds;
        }
        // 表示・ログ用：カメラ（頭）がいる階の値を代表値にする
        int vi = viewer != null ? FloorIndexAt(viewer.position.y) : -1;
        if (vi < 0) vi = 0;
        if (sorted.Count > 0)
        {
            LayerHeightAboveFloor = fLayerY[vi] - floorY;
            ExtinctionUpper = fK[vi];
            COppmUpper = fCO[vi];
            // いちばん下まで煙が来ている階の名前
            string cur = "";
            for (int i = 0; i < sorted.Count; i++) if (fStarted[i]) cur = sorted[i].name;
            CurrentFloorName = cur;
        }
    }

    /// <summary>高さ y が何番目の階か（sorted の番号、上の階が 0）。どの階でもなければ -1</summary>
    int FloorIndexAt(float y)
    {
        int best = -1; float bestFloor = float.MinValue;
        float tol = 0.05f * Upm;
        for (int i = 0; i < sorted.Count; i++)
        {
            var f = sorted[i];
            if (y >= f.floorY - tol && y <= f.ceilingY + tol) return i;
            // 床スラブの中などは、すぐ下の階として扱う
            if (y > f.ceilingY && f.floorY > bestFloor && i > 0 && y < sorted[i - 1].floorY) { best = i; bestFloor = f.floorY; }
        }
        return best;
    }

    /// <summary>位置 p での煙層の下端の高さ（ワールド Y）。その階に煙が無ければ天井の高さ</summary>
    public float LayerYAt(Vector3 p)
    {
        if (mode != Mode.FloorByFloor) return LayerY;
        int i = FloorIndexAt(p.y);
        return i >= 0 ? fLayerY[i] : LayerY;
    }

    /// <summary>その高さ（ワールド Y）の真下にある階の床の高さ。床からの頭の高さを出すのに使う</summary>
    public float LocalFloorY(float worldY)
    {
        float best = floorY;
        if (floors != null)
            foreach (var f in floors)
                if (f.floorY <= worldY + 0.01f && f.floorY > best) best = f.floorY;
        return best;
    }

    /// <summary>その位置が煙の範囲（regions）の中か。regions が空なら常に true</summary>
    public bool InsideRegions(Vector3 p)
    {
        if (regions == null || regions.Count == 0) return true;
        foreach (var r in regions)
            if (Mathf.Abs(p.x - r.centerXZ.x) <= r.sizeXZ.x * 0.5f && Mathf.Abs(p.z - r.centerXZ.y) <= r.sizeXZ.y * 0.5f)
                return true;
        return false;
    }

    /// <summary>位置 p での減光係数 [1/m]（建物の外なら 0）</summary>
    public float ExtinctionAt(Vector3 p) => InsideRegions(p) ? ExtinctionAt(p.y) : 0f;
    /// <summary>位置 p での CO 濃度 [ppm]（建物の外なら 0）</summary>
    public float COAt(Vector3 p) => InsideRegions(p) ? COAt(p.y) : 0f;
    /// <summary>位置 p が煙層の中か（0〜1、建物の外なら 0）</summary>
    public float UpperWeight(Vector3 p) => InsideRegions(p) ? UpperWeight(p.y) : 0f;

    /// <summary>高さ y（ワールド）での減光係数 [1/m]。境目はなめらかにつなぐ</summary>
    public float ExtinctionAt(float worldY)
    {
        if (mode == Mode.FloorByFloor && smokeEnabled)
        {
            int i = FloorIndexAt(worldY);
            if (i < 0 || !fStarted[i]) return 0f;
            return Mathf.Lerp(fK[i] * lowerLayerLeak, fK[i], FloorWeight(i, worldY));
        }
        float w = UpperWeight(worldY);
        return Mathf.Lerp(ExtinctionUpper * lowerLayerLeak, ExtinctionUpper, w);
    }

    /// <summary>高さ y（ワールド）での CO 濃度 [ppm]</summary>
    public float COAt(float worldY)
    {
        if (mode == Mode.FloorByFloor && smokeEnabled)
        {
            int i = FloorIndexAt(worldY);
            if (i < 0 || !fStarted[i]) return 0f;
            return Mathf.Lerp(fCO[i] * lowerLayerLeak, fCO[i], FloorWeight(i, worldY));
        }
        float w = UpperWeight(worldY);
        return Mathf.Lerp(COppmUpper * lowerLayerLeak, COppmUpper, w);
    }

    /// <summary>その高さが煙層の中かどうか（0〜1）</summary>
    public float UpperWeight(float worldY)
    {
        if (mode == Mode.FloorByFloor)
        {
            if (!smokeEnabled) return 0f;
            int i = FloorIndexAt(worldY);
            return (i < 0 || !fStarted[i]) ? 0f : FloorWeight(i, worldY);
        }
        if (!smokeEnabled || ExtinctionUpper <= 0f) return 0f;
        float b = interfaceBlend * Upm;
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(LayerY - b, LayerY + b, worldY));
    }

    float FloorWeight(int i, float worldY)
    {
        float b = interfaceBlend * Upm;
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(fLayerY[i] - b, fLayerY[i] + b, worldY));
    }

    void RefreshFloorVolumes()
    {
        foreach (var r in regions) if (r.volume != null) r.volume.gameObject.SetActive(false);
        if (interfaceParticles != null) { var em0 = interfaceParticles.emission; em0.enabled = false; }
        for (int ri = 0; ri < regions.Count; ri++)
        {
            var r = regions[ri];
            for (int i = 0; i < sorted.Count; i++)
            {
                var f = sorted[i];
                string key = r.name + "_" + f.name;
                if (!floorVolumes.TryGetValue(key, out var vol) || vol == null)
                {
                    vol = MakeVolume("SmokeVolume_" + key);
                    floorVolumes[key] = vol;
                }
                float top = f.ceilingY - 0.01f * Upm;
                float thick = top - fLayerY[i];
                bool show = smokeEnabled && fStarted[i] && fK[i] > 0.01f && thick > 0.02f * Upm;
                vol.gameObject.SetActive(show);
                if (!show) continue;
                vol.position = new Vector3(r.centerXZ.x, top - thick * 0.5f, r.centerXZ.y);
                vol.localScale = new Vector3(r.sizeXZ.x - 0.02f, thick, r.sizeXZ.y - 0.02f);
                SetVolumeAlpha(vol, 1f - Mathf.Exp(-fK[i] * 1.5f));
            }
        }
    }

    void SetVolumeAlpha(Transform vol, float alpha)
    {
        var rend = vol.GetComponent<Renderer>();
        if (rend == null) return;
        var mpb = new MaterialPropertyBlock();
        rend.GetPropertyBlock(mpb);
        var c = smokeColor; c.a = Mathf.Clamp01(alpha * 0.95f);
        mpb.SetColor("_BaseColor", c);
        mpb.SetColor("_Color", c);
        rend.SetPropertyBlock(mpb);
    }

    Transform MakeVolume(string name)
    {
        var existing = transform.Find(name);
        if (existing != null) return existing;
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        if (Application.isPlaying) Destroy(go.GetComponent<Collider>()); else DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(transform, true);
        var rend = go.GetComponent<Renderer>();
        if (smokeVolumeMaterial != null) rend.sharedMaterial = smokeVolumeMaterial;
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rend.receiveShadows = false;
        return go.transform;
    }

    void Refresh()
    {
        if (mode == Mode.FloorByFloor && sorted.Count > 0)
        {
            RefreshFloorVolumes();
            ApplyFog();
            return;
        }
        // 煙層の箱
        float thickness = Mathf.Max(0.001f, ceilingHeight - LayerHeightAboveFloor);
        bool show = smokeEnabled && ExtinctionUpper > 0.01f && thickness > 0.02f;
        float alpha = 1f - Mathf.Exp(-ExtinctionUpper * 1.5f); // 1.5m 先を見たときの不透明度の目安
        foreach (var r in regions)
        {
            if (r.volume == null) continue;
            r.volume.gameObject.SetActive(show);
            if (!show) continue;
            r.volume.position = new Vector3(r.centerXZ.x, floorY + ceilingHeight - thickness * 0.5f - 0.005f, r.centerXZ.y);
            r.volume.localScale = new Vector3(r.sizeXZ.x - 0.02f, thickness, r.sizeXZ.y - 0.02f);
            var rend = r.volume.GetComponent<Renderer>();
            if (rend != null)
            {
                var mpb = new MaterialPropertyBlock();
                rend.GetPropertyBlock(mpb);
                var c = smokeColor; c.a = Mathf.Clamp01(alpha * 0.95f);
                mpb.SetColor("_BaseColor", c);
                mpb.SetColor("_Color", c);
                rend.SetPropertyBlock(mpb);
            }
        }

        // 煙層下端のパーティクルを境目に追従させる
        if (interfaceParticles != null)
        {
            var p = interfaceParticles.transform.position;
            interfaceParticles.transform.position = new Vector3(p.x, LayerY, p.z);
            var em = interfaceParticles.emission;
            em.enabled = show;
        }

        ApplyFog();
    }

    void ApplyFog()
    {
        // 霧（頭の位置の煙の濃さ）
        float k = viewer != null ? ExtinctionAt(viewer.position) : 0f;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogColor = Color.Lerp(new Color(0.25f, 0.25f, 0.27f), smokeColor, Mathf.Clamp01(k));
        // 霧の濃さはワールド単位あたり（拡大した建物でも実寸の見通し距離になるよう換算）
        RenderSettings.fogDensity = (baseFogDensity + k * fogScale) / Upm;
    }

    void EnsureVolumes()
    {
        foreach (var r in regions)
        {
            if (r.volume != null) continue;
            var existing = transform.Find("SmokeVolume_" + r.name);
            if (existing != null) { r.volume = existing; continue; }
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "SmokeVolume_" + r.name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(transform, true);
            var rend = go.GetComponent<Renderer>();
            if (smokeVolumeMaterial != null) rend.sharedMaterial = smokeVolumeMaterial;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.receiveShadows = false;
            r.volume = go.transform;
        }
    }

    public string Describe() =>
        mode == Mode.FloorByFloor
            ? $"FloorByFloor;floors={floors.Count};next={nextFloorStartFraction};gap={floorGapSeconds}s;dens0={initialDensity};K={linearExtinction};CO={linearCOppm}ppm;upm={worldUnitsPerMeter}"
            : mode == Mode.Linear
            ? $"Linear;delay={linearDelay}s;dur={linearDuration}s;K={linearExtinction};CO={linearCOppm}ppm"
            : $"FirePlume;growth={growth}(a={Alpha(growth)});preburn={preburnSeconds}s;Qmax={maxHRR}kW;soot={sootYield};co={coYield};area={FloorArea:F1}m2;H={ceilingHeight}m;loss={heatLossFraction}";

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 0.2f, 0.2f, 0.6f);
        foreach (var r in regions)
            Gizmos.DrawWireCube(new Vector3(r.centerXZ.x, floorY + ceilingHeight * 0.5f, r.centerXZ.y),
                                new Vector3(r.sizeXZ.x, ceilingHeight, r.sizeXZ.y));
        // 各階の床（緑）と天井（赤）
        if (floors == null || regions == null || regions.Count == 0) return;
        foreach (var f in floors)
            foreach (var r in regions)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawWireCube(new Vector3(r.centerXZ.x, f.floorY, r.centerXZ.y), new Vector3(r.sizeXZ.x, 0.01f, r.sizeXZ.y));
                Gizmos.color = Color.red;
                Gizmos.DrawWireCube(new Vector3(r.centerXZ.x, f.ceilingY, r.centerXZ.y), new Vector3(r.sizeXZ.x, 0.01f, r.sizeXZ.y));
            }
    }
}
