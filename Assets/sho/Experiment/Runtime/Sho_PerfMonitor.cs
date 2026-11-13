using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.XR;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// 煙・視界不良の「重さ」を CSV に記録する（画面には何も表示しない）。
/// 1秒ごとに EvacLogs/perf_○○.csv に追記：
///   実測  … fps、フレーム時間（平均/最大）、CPU 時間、GPU 時間、バッチ数・SetPass 数・三角形数、パーティクル数
///   計算量の見積もり（式の各項）… 画素数 P、中心窩の割合 f、霧が覆う割合 c、実効サンプル数 N_eff、
///                               3D ノイズ読み取り回数 F = c·P·f·N_eff·2 [百万回/フレーム]
/// キーで見た目の方式を切り替えて、同じ場所で比べられる（エディタ用）：
///   F5 霧のみ / F6 黒い箱 / F7 板の重ね / F8 立体の霧
/// GPU 時間を Quest で取るには Player Settings > Other Settings > Frame Timing Stats をオン。
/// </summary>
public class Sho_PerfMonitor : MonoBehaviour
{
    public bool writeCsv = true;
    public SmokeLayerController smoke;
    public Sho_SmokeLayerVisual sliceVisual;
    public Sho_SmokeVolumeVisual volumeVisual;
    public Sho_FoveatedRendering foveation;

    public string Mode { get; private set; } = "";

    ProfilerRecorder batches, setPass, tris;
    readonly FrameTiming[] ft = new FrameTiming[1];
    float acc, accMax, timer; int frames;
    double cpuSum, gpuSum; int timingCount;
    string csvPath;
    static readonly CultureInfo IC = CultureInfo.InvariantCulture;

    void OnEnable()
    {
        batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
        setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
        tris = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
    }

    void OnDisable() { batches.Dispose(); setPass.Dispose(); tris.Dispose(); }

    void Start()
    {
        if (smoke == null) smoke = FindAnyObjectByType<SmokeLayerController>();
        if (sliceVisual == null) sliceVisual = FindAnyObjectByType<Sho_SmokeLayerVisual>(FindObjectsInactive.Include);
        if (volumeVisual == null) volumeVisual = FindAnyObjectByType<Sho_SmokeVolumeVisual>(FindObjectsInactive.Include);
        if (foveation == null) foveation = FindAnyObjectByType<Sho_FoveatedRendering>(FindObjectsInactive.Include);
        Mode = DetectMode();
        if (!writeCsv) return;
        string dir = Path.Combine(Application.persistentDataPath, "EvacLogs");
        Directory.CreateDirectory(dir);
        csvPath = Path.Combine(dir, $"perf_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        File.WriteAllText(csvPath,
            "time_s,mode,fps,avg_ms,max_ms,cpu_ms,gpu_ms,batches,setpass,tris,particles," +
            "P_pixels,f_foveation,c_coverage,N_eff,F_fetches_M,head_in_smoke,fog_density\n", Encoding.UTF8);
    }

    string DetectMode()
    {
        if (volumeVisual != null && volumeVisual.isActiveAndEnabled) return "volume";
        if (sliceVisual != null && sliceVisual.isActiveAndEnabled) return "slices";
        if (smoke != null && smoke.useBoxVolumes) return "box";
        return "fog_only";
    }

    public void SetMode(string mode)
    {
        if (sliceVisual != null) sliceVisual.gameObject.SetActive(mode == "slices");
        if (volumeVisual != null) volumeVisual.gameObject.SetActive(mode == "volume");
        if (smoke != null) { smoke.useBoxVolumes = mode == "box"; if (mode != "volume") smoke.volumeVisualActive = false; }
        Mode = mode;
    }

    /// <summary>1フレームに描く画素数 P（両目分）</summary>
    static float Pixels()
    {
        if (XRSettings.isDeviceActive && XRSettings.eyeTextureWidth > 0)
            return XRSettings.eyeTextureWidth * XRSettings.eyeTextureHeight * 2f; // 両目
        return Screen.width * (float)Screen.height;
    }

    void Update()
    {
#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current;
        if (k != null)
        {
            if (k.f5Key.wasPressedThisFrame) SetMode("fog_only");
            if (k.f6Key.wasPressedThisFrame) SetMode("box");
            if (k.f7Key.wasPressedThisFrame) SetMode("slices");
            if (k.f8Key.wasPressedThisFrame) SetMode("volume");
        }
#endif
        float ms = Time.unscaledDeltaTime * 1000f;
        acc += ms; accMax = Mathf.Max(accMax, ms); frames++; timer += Time.unscaledDeltaTime;
        FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.GetLatestTimings(1, ft) > 0) { cpuSum += ft[0].cpuFrameTime; gpuSum += ft[0].gpuFrameTime; timingCount++; }

        if (timer < 1f) return;
        float fps = frames / timer, avgMs = acc / Mathf.Max(1, frames), maxMs = accMax;
        double cpuMs = timingCount > 0 ? cpuSum / timingCount : 0, gpuMs = timingCount > 0 ? gpuSum / timingCount : 0;
        acc = accMax = timer = 0f; frames = 0; cpuSum = gpuSum = 0; timingCount = 0;
        if (!writeCsv || csvPath == null) return;

        int particles = FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None).Sum(p => p.particleCount);
        float P = Pixels();
        float f = foveation != null && foveation.isActiveAndEnabled && foveation.Applied ? foveation.PixelFraction : 1f;
        bool vol = volumeVisual != null && volumeVisual.isActiveAndEnabled;
        float c = vol ? volumeVisual.EstimatedCoverage : 0f;
        float n = vol ? volumeVisual.EffectiveSteps : 0f;
        float F = c * P * f * n * 2f / 1e6f; // 3D ノイズ読み取り [百万回/フレーム]
        var cam = Camera.main;
        float inSmoke = smoke != null && cam != null ? smoke.UpperWeight(cam.transform.position) : 0f;

        string line = string.Join(",",
            Time.time.ToString("F1", IC), Mode, fps.ToString("F1", IC), avgMs.ToString("F2", IC), maxMs.ToString("F2", IC),
            cpuMs.ToString("F2", IC), gpuMs.ToString("F2", IC), Rec(batches), Rec(setPass), Rec(tris), particles.ToString(IC),
            P.ToString("F0", IC), f.ToString("F2", IC), c.ToString("F3", IC), n.ToString("F1", IC), F.ToString("F2", IC),
            inSmoke.ToString("F2", IC), RenderSettings.fogDensity.ToString("F4", IC));
        try { File.AppendAllText(csvPath, line + "\n"); } catch { }
    }

    static string Rec(ProfilerRecorder r) => r.Valid ? r.LastValue.ToString(IC) : "";
}
