using UnityEngine;

/// <summary>
/// 火元の見た目。SmokeLayerController の発熱速度(HRR)に合わせて、
/// 炎のパーティクルの量と、ゆらめくライトの明るさを変える。
/// </summary>
public class FireVisual : MonoBehaviour
{
    public SmokeLayerController smoke;
    public ParticleSystem flames;
    public Light fireLight;
    [Tooltip("この発熱速度[kW]で炎が最大の見た目になる")]
    public float fullHRR = 1000f;
    public float maxEmission = 120f;
    public float maxLightIntensity = 6f;

    void Update()
    {
        if (smoke == null) return;
        float s = smoke.smokeEnabled ? Mathf.Sqrt(Mathf.Clamp01(smoke.HRR / Mathf.Max(1f, fullHRR))) : 0f;
        if (smoke.mode != SmokeLayerController.Mode.FirePlume && smoke.smokeEnabled) s = 0.6f;
        if (flames != null)
        {
            var em = flames.emission;
            em.rateOverTime = maxEmission * s;
            var main = flames.main;
            main.startSizeMultiplier = Mathf.Lerp(0.3f, 1.2f, s);
        }
        if (fireLight != null)
        {
            float flicker = 0.8f + 0.4f * Mathf.PerlinNoise(Time.time * 8f, 0.5f);
            fireLight.intensity = maxLightIntensity * s * flicker;
        }
    }
}
