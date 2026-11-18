using UnityEngine;

/// <summary>
/// 火元から立ち上る煙（プルーム）と、天井にぶつかって横に広がる煙（天井流）の見た目。
/// 火の大きさ（SmokeLayerController の HRR、または FireSource / 一定値）に合わせて煙の量を変える。
/// 火元の子に置く。plume / ceilingJet はセットアップのメニューで自動で作られる。
/// </summary>
public class Sho_SmokePlume : MonoBehaviour
{
    public SmokeLayerController smoke;
    public ParticleSystem plume;
    public ParticleSystem ceilingJet;
    [Tooltip("この発熱速度 [kW] で煙の量が最大になる")]
    public float fullHRR = 1000f;
    [Tooltip("HRR が分からないモード（Linear / FloorByFloor）のときの量（0〜1）")]
    [Range(0f, 1f)] public float defaultLevel = 0.7f;
    public float maxPlumeRate = 25f;
    public float maxJetRate = 12f;

    void Update()
    {
        if (smoke == null) return;
        float level = 0f;
        if (smoke.smokeEnabled)
            level = smoke.mode == SmokeLayerController.Mode.FirePlume
                ? Mathf.Sqrt(Mathf.Clamp01(smoke.HRR / Mathf.Max(1f, fullHRR)))
                : defaultLevel;

        if (plume != null) { var em = plume.emission; em.rateOverTime = maxPlumeRate * level; }
        if (ceilingJet != null)
        {
            var em = ceilingJet.emission; em.rateOverTime = maxJetRate * level;
            // 天井のすぐ下に置く
            float ceil = smoke.CeilingYAt(transform.position.y + 0.1f);
            var p = ceilingJet.transform.position;
            ceilingJet.transform.position = new Vector3(p.x, ceil - 0.25f * Mathf.Max(0.01f, smoke.worldUnitsPerMeter), p.z);
        }
    }
}
