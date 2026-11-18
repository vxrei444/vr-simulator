using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 煙層をリアルに見せる（見た目だけ。煙の計算とログは SmokeLayerController のまま）。
///   ・煙層の下端に、まだら模様の半透明の板を何枚か重ねる（下ほど薄く・隙間が多い／上ほど濃い）
///   ・板の高さをゆっくり上下させ、境目が波打つように見せる
///   ・下端に沿って、大きく薄い煙の粒（パーティクル）を漂わせる
/// SmokeLayerController の各階（FloorByFloor）・各範囲（regions）ごとに自動で作られる。
/// </summary>
public class Sho_SmokeLayerVisual : MonoBehaviour
{
    public SmokeLayerController smoke;

    [Header("重ねる板")]
    [Tooltip("Sho/SmokeSlice シェーダーの材質")]
    public Material sliceMaterial;
    [Range(1, 8)] public int sliceCount = 8;
    [Tooltip("煙層の下端から上へ、板を並べる厚さ [m]")]
    public float sliceSpanM = 1.3f;
    [Range(0f, 1f)] public float bottomAlpha = 0.12f;
    [Range(0f, 1f)] public float topAlpha = 0.75f;
    [Range(0f, 1f)] public float bottomCoverage = 0.35f;
    [Range(0f, 1f)] public float topCoverage = 0.9f;
    [Tooltip("境目が上下に揺れる幅 [m]")]
    public float bobAmplitudeM = 0.06f;
    public float bobSpeed = 0.35f;
    [Tooltip("煙の濃さ K がこの値のとき、板が最大の濃さになる [1/m]")]
    public float fullDensityK = 2f;

    [Header("境目の煙の粒")]
    public bool interfaceParticles = true;
    [Tooltip("煙の粒の材質（URP Particles/Unlit、煙のテクスチャ）")]
    public Material particleMaterial;
    [Tooltip("床面積 10m² あたりの最大粒数")]
    public float particlesPer10m2 = 1.2f;
    public int maxParticlesPerRegion = 80;
    public Vector2 particleSizeM = new Vector2(1.2f, 2.4f);
    public float particleLifetime = 9f;

    [Header("色")]
    public Color smokeColor = new Color(0.16f, 0.15f, 0.14f, 1f);

    class Layer
    {
        public Transform root;
        public Renderer[] slices;
        public ParticleSystem ps;
        public int floor, region;
    }

    readonly List<Layer> layers = new List<Layer>();
    MaterialPropertyBlock mpb;
    float Upm => smoke != null ? Mathf.Max(0.01f, smoke.worldUnitsPerMeter) : 1f;

    static readonly int IdColor = Shader.PropertyToID("_BaseColor");
    static readonly int IdAlpha = Shader.PropertyToID("_Alpha");
    static readonly int IdCoverage = Shader.PropertyToID("_Coverage");
    static readonly int IdUpm = Shader.PropertyToID("_UnitsPerMeter");
    static readonly int IdSeed = Shader.PropertyToID("_Seed");

    void Start()
    {
        if (smoke == null) smoke = FindAnyObjectByType<SmokeLayerController>();
        if (smoke == null) { enabled = false; return; }
        smoke.useBoxVolumes = false;
        mpb = new MaterialPropertyBlock();
        Build();
    }

    void Build()
    {
        foreach (var l in layers) if (l.root != null) Destroy(l.root.gameObject);
        layers.Clear();
        int nl = smoke.VisualLayerCount;
        for (int f = 0; f < nl; f++)
        {
            for (int r = 0; r < smoke.regions.Count; r++)
            {
                var reg = smoke.regions[r];
                var root = new GameObject($"SmokeVisual_{reg.name}_{f}").transform;
                root.SetParent(transform, false);
                var l = new Layer { root = root, floor = f, region = r, slices = new Renderer[sliceCount] };
                for (int s = 0; s < sliceCount; s++)
                {
                    var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    q.name = "Slice" + s;
                    Destroy(q.GetComponent<Collider>());
                    q.transform.SetParent(root, false);
                    q.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                    q.transform.localScale = new Vector3(reg.sizeXZ.x, reg.sizeXZ.y, 1f);
                    var rend = q.GetComponent<Renderer>();
                    rend.sharedMaterial = sliceMaterial;
                    rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    rend.receiveShadows = false;
                    l.slices[s] = rend;
                }
                if (interfaceParticles && particleMaterial != null) l.ps = MakeParticles(root, reg);
                layers.Add(l);
            }
        }
    }

    ParticleSystem MakeParticles(Transform parent, SmokeLayerController.Region reg)
    {
        var go = new GameObject("InterfaceParticles");
        go.transform.SetParent(parent, false);
        go.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // 箱の Z（厚み）を上下に
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        float upm = Upm;
        float areaM2 = reg.sizeXZ.x * reg.sizeXZ.y / (upm * upm);
        int max = Mathf.Clamp(Mathf.RoundToInt(areaM2 / 10f * particlesPer10m2), 8, maxParticlesPerRegion);

        var main = ps.main;
        main.loop = true;
        main.startLifetime = particleLifetime;
        main.startSpeed = 0.02f * upm;
        main.startSize = new ParticleSystem.MinMaxCurve(particleSizeM.x * upm, particleSizeM.y * upm);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.startColor = new Color(smokeColor.r, smokeColor.g, smokeColor.b, 0.45f);
        main.maxParticles = max;
        main.simulationSpace = ParticleSystemSimulationSpace.Local; // 煙層と一緒に下がる
        main.playOnAwake = true;

        var em = ps.emission;
        em.rateOverTime = max / Mathf.Max(1f, particleLifetime);

        var sh = ps.shape;
        sh.shapeType = ParticleSystemShapeType.Box;
        sh.scale = new Vector3(reg.sizeXZ.x * 0.95f, reg.sizeXZ.y * 0.95f, 0.15f * upm);

        var rot = ps.rotationOverLifetime;
        rot.enabled = true;
        rot.z = new ParticleSystem.MinMaxCurve(-0.15f, 0.15f);

        var col = ps.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                  new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.25f), new GradientAlphaKey(1f, 0.7f), new GradientAlphaKey(0f, 1f) });
        col.color = g;

        var sol = ps.sizeOverLifetime;
        sol.enabled = true;
        sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.8f, 1f, 1.3f));

        var pr = go.GetComponent<ParticleSystemRenderer>();
        pr.sharedMaterial = particleMaterial;
        pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        pr.receiveShadows = false;
        pr.renderMode = ParticleSystemRenderMode.Billboard;
        ps.Play();
        return ps;
    }

    void LateUpdate()
    {
        if (smoke == null) return;
        if (layers.Count != smoke.VisualLayerCount * smoke.regions.Count) Build();
        float upm = Upm, t = Time.time;
        foreach (var l in layers)
        {
            smoke.GetVisualLayer(l.floor, out float ceilY, out float layerY, out float k, out bool active);
            var reg = smoke.regions[l.region];
            float thick = ceilY - layerY;
            bool show = active && thick > 0.02f * upm;
            l.root.gameObject.SetActive(show);
            if (!show) continue;

            float dens = Mathf.Clamp01(k / Mathf.Max(0.01f, fullDensityK));
            float span = Mathf.Min(sliceSpanM * upm, thick - 0.02f * upm);
            for (int s = 0; s < l.slices.Length; s++)
            {
                float u = l.slices.Length > 1 ? s / (float)(l.slices.Length - 1) : 0f;
                float bob = Mathf.Sin(t * bobSpeed + s * 1.7f + l.region * 0.9f) * bobAmplitudeM * upm * (1f - u * 0.6f);
                float y = Mathf.Min(ceilY - 0.01f * upm, layerY + u * span + bob);
                var tr = l.slices[s].transform;
                tr.position = new Vector3(reg.centerXZ.x, y, reg.centerXZ.y);
                tr.localScale = new Vector3(reg.sizeXZ.x, reg.sizeXZ.y, 1f);

                l.slices[s].GetPropertyBlock(mpb);
                mpb.SetColor(IdColor, smokeColor);
                mpb.SetFloat(IdAlpha, Mathf.Lerp(bottomAlpha, topAlpha, u) * Mathf.Lerp(0.35f, 1f, dens));
                mpb.SetFloat(IdCoverage, Mathf.Lerp(bottomCoverage, topCoverage, u) * Mathf.Lerp(0.7f, 1f, dens));
                mpb.SetFloat(IdUpm, upm);
                mpb.SetFloat(IdSeed, s * 3.1f + l.region * 7.7f + l.floor * 13.3f);
                l.slices[s].SetPropertyBlock(mpb);
            }
            if (l.ps != null)
            {
                l.ps.transform.position = new Vector3(reg.centerXZ.x, layerY + 0.05f * upm, reg.centerXZ.y);
                var main = l.ps.main;
                main.startColor = new Color(smokeColor.r, smokeColor.g, smokeColor.b, 0.25f + 0.3f * dens);
            }
        }
    }
}
