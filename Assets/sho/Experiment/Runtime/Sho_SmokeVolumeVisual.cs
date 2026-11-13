using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 煙層を「立体の霧」（Sho/SmokeVolume シェーダー）で描く。見た目だけで、計算とログは SmokeLayerController のまま。
///   ・煙層ごと・範囲（regions）ごとに、煙層を囲む箱を1つ置き、その中を視線に沿って数か所調べて濃さを足し合わせる
///   ・境目の凹凸・濃さのむら・ゆっくりした流れは 3D ノイズで表現
///   ・火元から広がる（SmokeLayerController の spatialSpread がオンのとき）
///   ・自動画質：フレーム時間が目標を超えたらサンプル数を減らし、余裕があれば戻す（Quest 向けの軽量化）
/// ※カメラの「Depth Texture」をオンにすること（セットアップのメニューが自動で設定する）。
/// </summary>
public class Sho_SmokeVolumeVisual : MonoBehaviour
{
    public SmokeLayerController smoke;
    [Tooltip("Sho/SmokeVolume シェーダーの材質")]
    public Material volumeMaterial;

    [Header("画質と重さ")]
    [Tooltip("視線に沿って調べる数（多いほどきれい・重い）")]
    [Range(4, 24)] public int steps = 10;
    [Tooltip("フレーム時間に合わせてサンプル数を自動で増減する")]
    public bool autoQuality = true;
    [Tooltip("目標のフレーム時間 [ms]（Quest 3: 72Hz=13.9 / 90Hz=11.1）")]
    public float targetFrameMs = 13.9f;
    [Range(4, 24)] public int minSteps = 6;
    [Range(4, 24)] public int maxSteps = 14;
    [Tooltip("頭が煙層の中にいるときのサンプル数（煙の中では霧が見えにくさを担当するので粗くてよい）")]
    [Range(2, 24)] public int insideSteps = 4;

    [Header("見た目")]
    public Color smokeColor = new Color(0.16f, 0.15f, 0.14f, 1f);
    [Tooltip("見た目の濃さの倍率（1 = 計算どおりの減光係数）")]
    [Range(0.1f, 3f)] public float visualScale = 1f;
    [Tooltip("境目の凹凸 [m]")]
    public float waveAmplitudeM = 0.35f;
    [Tooltip("境目のぼかし [m]")]
    public float blendM = 0.3f;

    [Header("境目の煙の粒（任意）")]
    public Material particleMaterial;
    public bool interfaceParticles = true;
    public int particlesPerRegion = 40;

    class Box { public Renderer rend; public ParticleSystem ps; public int layer, region; }
    readonly List<Box> boxes = new List<Box>();
    MaterialPropertyBlock mpb;
    float avgMs = 0f;
    float qualityTimer;

    /// <summary>自動画質で決まったサンプル数</summary>
    public int CurrentSteps { get; private set; }
    /// <summary>煙の中かどうかも考えた、実際に使うサンプル数 N_eff</summary>
    public float EffectiveSteps { get; private set; }
    /// <summary>画面のうち立体の霧が覆っている割合の見積もり c（0〜1）</summary>
    public float EstimatedCoverage { get; private set; }
    /// <summary>表示中の箱の数</summary>
    public int ActiveBoxes { get; private set; }

    static readonly int IdBoxMin = Shader.PropertyToID("_BoxMin"), IdBoxMax = Shader.PropertyToID("_BoxMax"),
        IdLayer = Shader.PropertyToID("_LayerY"), IdCeil = Shader.PropertyToID("_CeilY"), IdK = Shader.PropertyToID("_K"),
        IdUpm = Shader.PropertyToID("_Upm"), IdOrigin = Shader.PropertyToID("_Origin"), IdFront = Shader.PropertyToID("_FrontM"),
        IdTaper = Shader.PropertyToID("_TaperM"), IdSeed = Shader.PropertyToID("_Seed"), IdSteps = Shader.PropertyToID("_Steps"),
        IdColor = Shader.PropertyToID("_BaseColor"), IdScale = Shader.PropertyToID("_VisualScale"),
        IdWave = Shader.PropertyToID("_WaveAmpM"), IdBlend = Shader.PropertyToID("_BlendM");

    void Start()
    {
        if (smoke == null) smoke = FindAnyObjectByType<SmokeLayerController>();
        if (smoke == null || volumeMaterial == null) { enabled = false; return; }
        smoke.useBoxVolumes = false;
        smoke.volumeVisualActive = true;
        mpb = new MaterialPropertyBlock();
        CurrentSteps = steps;
        Build();
    }

    void OnDisable() { if (smoke != null) smoke.volumeVisualActive = false; }
    void OnEnable() { if (smoke != null && mpb != null) smoke.volumeVisualActive = true; }

    void Build()
    {
        foreach (var b in boxes) if (b.rend != null) Destroy(b.rend.gameObject);
        boxes.Clear();
        for (int l = 0; l < smoke.VisualLayerCount; l++)
            for (int r = 0; r < smoke.regions.Count; r++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"SmokeVolume3D_{smoke.regions[r].name}_{l}";
                Destroy(go.GetComponent<Collider>());
                go.transform.SetParent(transform, false);
                var rend = go.GetComponent<Renderer>();
                rend.sharedMaterial = volumeMaterial;
                rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                rend.receiveShadows = false;
                var b = new Box { rend = rend, layer = l, region = r };
                if (interfaceParticles && particleMaterial != null) b.ps = MakeParticles(go.transform.parent, smoke.regions[r]);
                boxes.Add(b);
            }
    }

    ParticleSystem MakeParticles(Transform parent, SmokeLayerController.Region reg)
    {
        float upm = Mathf.Max(0.01f, smoke.worldUnitsPerMeter);
        var go = new GameObject("InterfaceParticles_" + reg.name);
        go.transform.SetParent(parent, false);
        go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.startLifetime = 9f;
        main.startSpeed = 0.02f * upm;
        main.startSize = new ParticleSystem.MinMaxCurve(1.2f * upm, 2.4f * upm);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.startColor = new Color(smokeColor.r, smokeColor.g, smokeColor.b, 0.35f);
        main.maxParticles = particlesPerRegion;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        var em = ps.emission; em.rateOverTime = particlesPerRegion / 9f;
        var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Box;
        sh.scale = new Vector3(reg.sizeXZ.x * 0.95f, reg.sizeXZ.y * 0.95f, 0.15f * upm);
        var rot = ps.rotationOverLifetime; rot.enabled = true; rot.z = new ParticleSystem.MinMaxCurve(-0.15f, 0.15f);
        var col = ps.colorOverLifetime; col.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                  new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.25f), new GradientAlphaKey(1f, 0.7f), new GradientAlphaKey(0f, 1f) });
        col.color = g;
        var pr = go.GetComponent<ParticleSystemRenderer>();
        pr.sharedMaterial = particleMaterial;
        pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        ps.Play();
        return ps;
    }

    void Update()
    {
        if (!autoQuality) { CurrentSteps = steps; return; }
        // フレーム時間の移動平均で、サンプル数を 0.5 秒ごとに 1 ずつ調整
        float ms = Time.unscaledDeltaTime * 1000f;
        avgMs = avgMs <= 0f ? ms : Mathf.Lerp(avgMs, ms, 0.1f);
        qualityTimer += Time.unscaledDeltaTime;
        if (qualityTimer < 0.5f) return;
        qualityTimer = 0f;
        if (avgMs > targetFrameMs * 1.05f && CurrentSteps > minSteps) CurrentSteps--;
        else if (avgMs < targetFrameMs * 0.85f && CurrentSteps < maxSteps) CurrentSteps++;
    }

    void LateUpdate()
    {
        if (smoke == null) return;
        if (boxes.Count != smoke.VisualLayerCount * smoke.regions.Count) Build();
        float upm = Mathf.Max(0.01f, smoke.worldUnitsPerMeter);
        int active = 0;
        var cam = Camera.main;
        // 頭が煙層の中にいる度合い w（0〜1）で、サンプル数を insideSteps まで減らす
        float w = cam != null ? smoke.UpperWeight(cam.transform.position) : 0f;
        EffectiveSteps = Mathf.Lerp(CurrentSteps, Mathf.Min(insideSteps, CurrentSteps), w);
        float coverage = 0f;
        foreach (var b in boxes)
        {
            smoke.GetVisualLayer(b.layer, out float ceilY, out float layerY, out float k, out bool on);
            smoke.GetSpread(b.layer, out Vector3 origin, out float frontM);
            var reg = smoke.regions[b.region];
            bool show = on && ceilY - layerY > 0.01f * upm && frontM > 0.05f;
            b.rend.gameObject.SetActive(show);
            if (b.ps != null) b.ps.gameObject.SetActive(show);
            if (!show) continue;
            active++;

            float margin = (waveAmplitudeM + blendM) * upm;
            float bottom = layerY - margin;
            var bmin = new Vector3(reg.centerXZ.x - reg.sizeXZ.x * 0.5f, bottom, reg.centerXZ.y - reg.sizeXZ.y * 0.5f);
            var bmax = new Vector3(reg.centerXZ.x + reg.sizeXZ.x * 0.5f, ceilY, reg.centerXZ.y + reg.sizeXZ.y * 0.5f);
            var t = b.rend.transform;
            t.position = (bmin + bmax) * 0.5f;
            t.rotation = Quaternion.identity;
            t.localScale = bmax - bmin;

            b.rend.GetPropertyBlock(mpb);
            mpb.SetVector(IdBoxMin, bmin);
            mpb.SetVector(IdBoxMax, bmax);
            mpb.SetFloat(IdLayer, layerY);
            mpb.SetFloat(IdCeil, ceilY);
            mpb.SetFloat(IdK, k);
            mpb.SetFloat(IdUpm, upm);
            mpb.SetVector(IdOrigin, origin);
            mpb.SetFloat(IdFront, Mathf.Min(frontM, 1e5f));
            mpb.SetFloat(IdTaper, smoke.spatialSpread ? smoke.taperM : 0f);
            mpb.SetFloat(IdSeed, b.region * 7.7f + b.layer * 13.3f);
            mpb.SetFloat(IdSteps, Mathf.Max(2f, Mathf.Round(EffectiveSteps)));
            coverage += ScreenCoverage(cam, bmin, bmax);
            mpb.SetColor(IdColor, smokeColor);
            mpb.SetFloat(IdScale, visualScale);
            mpb.SetFloat(IdWave, waveAmplitudeM);
            mpb.SetFloat(IdBlend, blendM);
            b.rend.SetPropertyBlock(mpb);

            if (b.ps != null) b.ps.transform.position = new Vector3(reg.centerXZ.x, layerY + 0.05f * upm, reg.centerXZ.y);
        }
        ActiveBoxes = active;
        EstimatedCoverage = Mathf.Clamp01(coverage);
    }

    // 箱が画面に占める割合（8 頂点の画面上の外接長方形。カメラが箱の中・後ろにかかるときは 1）
    static float ScreenCoverage(Camera cam, Vector3 bmin, Vector3 bmax)
    {
        if (cam == null) return 0f;
        Vector3 c = cam.transform.position;
        if (c.x >= bmin.x && c.x <= bmax.x && c.y >= bmin.y && c.y <= bmax.y && c.z >= bmin.z && c.z <= bmax.z) return 1f;
        float x0 = 1f, y0 = 1f, x1 = 0f, y1 = 0f;
        for (int i = 0; i < 8; i++)
        {
            var p = new Vector3((i & 1) == 0 ? bmin.x : bmax.x, (i & 2) == 0 ? bmin.y : bmax.y, (i & 4) == 0 ? bmin.z : bmax.z);
            var v = cam.WorldToViewportPoint(p);
            if (v.z <= 0f) return 1f;
            x0 = Mathf.Min(x0, v.x); y0 = Mathf.Min(y0, v.y); x1 = Mathf.Max(x1, v.x); y1 = Mathf.Max(y1, v.y);
        }
        return Mathf.Clamp01(Mathf.Min(1f, x1) - Mathf.Max(0f, x0)) * Mathf.Clamp01(Mathf.Min(1f, y1) - Mathf.Max(0f, y0));
    }
}
