using UnityEngine;

/// <summary>
/// 煙の中で照明・誘導灯の周りの光がにじんで見える「光の輪」。
/// その場所の煙が濃いほど、輪が大きく・明るくなる（煙が無いときは見えない）。
/// 光源（Light）や看板（FlashingBeacon）の子に置き、常にカメラの方を向く。
/// </summary>
public class Sho_SmokeLightHalo : MonoBehaviour
{
    public SmokeLayerController smoke;
    [Tooltip("色と明るさを合わせる光源（任意）")]
    public Light sourceLight;
    [Tooltip("色と点滅を合わせる看板（任意。点滅に合わせて輪も明滅する）")]
    public FlashingBeacon beacon;
    public Renderer haloRenderer;
    public Color color = Color.white;
    [Tooltip("煙が薄いときの輪の大きさ [m]")]
    public float minSizeM = 0.3f;
    [Tooltip("煙が濃いときの輪の大きさ [m]")]
    public float maxSizeM = 2.5f;
    [Range(0f, 1f)] public float maxAlpha = 0.55f;
    [Tooltip("この K [1/m] で輪が最大になる")]
    public float fullK = 2f;

    MaterialPropertyBlock mpb;
    float baseIntensity = 1f;
    Transform cam;

    void Start()
    {
        if (smoke == null) smoke = FindAnyObjectByType<SmokeLayerController>();
        if (haloRenderer == null) haloRenderer = GetComponent<Renderer>();
        if (sourceLight != null) baseIntensity = Mathf.Max(0.01f, sourceLight.intensity);
        mpb = new MaterialPropertyBlock();
    }

    void LateUpdate()
    {
        if (haloRenderer == null) return;
        if (cam == null && Camera.main != null) cam = Camera.main.transform;
        if (smoke == null || cam == null) { haloRenderer.enabled = false; return; }

        float k = smoke.ExtinctionAt(transform.position);
        float f = Mathf.Clamp01(k / Mathf.Max(0.01f, fullK));
        float level = 1f;
        Color c = color;
        if (beacon != null) { level = Mathf.Lerp(beacon.offLevel, 1f, beacon.CurrentLevel); c = beacon.color; }
        else if (sourceLight != null) { level = Mathf.Clamp01(sourceLight.intensity / baseIntensity); c = sourceLight.color; }

        float a = maxAlpha * f * level;
        haloRenderer.enabled = a > 0.003f;
        if (!haloRenderer.enabled) return;

        float upm = Mathf.Max(0.01f, smoke.worldUnitsPerMeter);
        float size = Mathf.Lerp(minSizeM, maxSizeM, f) * upm;
        var p = transform.parent;
        Vector3 ps = p != null ? p.lossyScale : Vector3.one;
        transform.localScale = new Vector3(size / Mathf.Max(1e-4f, Mathf.Abs(ps.x)), size / Mathf.Max(1e-4f, Mathf.Abs(ps.y)), 1f);
        transform.rotation = Quaternion.LookRotation(transform.position - cam.position, Vector3.up); // Quad の表をカメラへ

        c.a = a;
        haloRenderer.GetPropertyBlock(mpb);
        mpb.SetColor("_BaseColor", c);
        mpb.SetColor("_Color", c);
        haloRenderer.SetPropertyBlock(mpb);
    }
}
