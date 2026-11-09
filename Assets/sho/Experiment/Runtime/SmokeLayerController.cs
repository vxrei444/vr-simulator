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
/// </summary>
public class SmokeLayerController : MonoBehaviour
{
    public enum Mode { FirePlume, Linear }
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

    [Header("モード")]
    public Mode mode = Mode.FirePlume;
    [Tooltip("煙を出すかどうか（煙なし条件では false）")]
    public bool smokeEnabled = true;

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

    [Header("直線降下（Linear モード）")]
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

    // ---- 内部状態（ゾーンモデル） ----
    const float Ta = 293.15f, Cp = 1.0f, KmSoot = 8700f, RhoCO = 1.145f, SimDt = 0.1f;
    float simT, layerMass, layerHeat, fuelMass;
    float baseCeiling;

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
        if (m != null && m.State != EvacuationState.Waiting) sessionT = m.ElapsedTime;
        // 計測終了後は煙も止める（結果の再現性のため）
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
        if (mode == Mode.Linear)
        {
            simT = t; FireTime = t;
            float u = Mathf.Clamp01((t - preburnSeconds - linearDelay) / Mathf.Max(1f, linearDuration));
            if (t - preburnSeconds < 0f) u = 0f;
            LayerHeightAboveFloor = Mathf.Lerp(ceilingHeight, minLayerHeight, u);
            ExtinctionUpper = u > 0f ? linearExtinction : 0f;
            COppmUpper = u > 0f ? linearCOppm : 0f;
            HRR = 0f;
            return;
        }

        float alpha = Alpha(growth);
        while (simT < t)
        {
            float dt = Mathf.Min(SimDt, t - simT);
            float Q = Mathf.Min(alpha * simT * simT, maxHRR);
            float Qc = convectiveFraction * Q;
            float z = LayerHeightAboveFloor; // 火源は床面にあると仮定
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
            LayerHeightAboveFloor = Mathf.Max(minLayerHeight, ceilingHeight - V / FloorArea);
            float Vlayer = Mathf.Max(0.5f, (ceilingHeight - LayerHeightAboveFloor) * FloorArea);
            ExtinctionUpper = KmSoot * sootYield * fuelMass / Mathf.Max(V, Vlayer);
            COppmUpper = coYield * fuelMass / Mathf.Max(V, Vlayer) / RhoCO * 1e6f;
            SmokeTempC = Ts - 273.15f;
            HRR = Q;
            simT += dt;
        }
        FireTime = simT;
    }

    /// <summary>高さ y（ワールド）での減光係数 [1/m]。境目はなめらかにつなぐ</summary>
    public float ExtinctionAt(float worldY)
    {
        float w = UpperWeight(worldY);
        return Mathf.Lerp(ExtinctionUpper * lowerLayerLeak, ExtinctionUpper, w);
    }

    /// <summary>高さ y（ワールド）での CO 濃度 [ppm]</summary>
    public float COAt(float worldY)
    {
        float w = UpperWeight(worldY);
        return Mathf.Lerp(COppmUpper * lowerLayerLeak, COppmUpper, w);
    }

    /// <summary>その高さが煙層の中かどうか（0〜1）</summary>
    public float UpperWeight(float worldY)
    {
        if (!smokeEnabled || ExtinctionUpper <= 0f) return 0f;
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(LayerY - interfaceBlend, LayerY + interfaceBlend, worldY));
    }

    void Refresh()
    {
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

        // 霧（頭の位置の煙の濃さ）
        float k = viewer != null ? ExtinctionAt(viewer.position.y) : 0f;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogColor = Color.Lerp(new Color(0.25f, 0.25f, 0.27f), smokeColor, Mathf.Clamp01(k));
        RenderSettings.fogDensity = baseFogDensity + k * fogScale;
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
        mode == Mode.Linear
            ? $"Linear;delay={linearDelay}s;dur={linearDuration}s;K={linearExtinction};CO={linearCOppm}ppm"
            : $"FirePlume;growth={growth}(a={Alpha(growth)});preburn={preburnSeconds}s;Qmax={maxHRR}kW;soot={sootYield};co={coYield};area={FloorArea:F1}m2;H={ceilingHeight}m;loss={heatLossFraction}";

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 0.2f, 0.2f, 0.6f);
        foreach (var r in regions)
            Gizmos.DrawWireCube(new Vector3(r.centerXZ.x, floorY + ceilingHeight * 0.5f, r.centerXZ.y),
                                new Vector3(r.sizeXZ.x, ceilingHeight, r.sizeXZ.y));
    }
}
