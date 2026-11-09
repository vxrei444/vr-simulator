using System.Globalization;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// 頭が煙層に入ったとき（立ったまま黒煙の中を歩く／煙を擦る）に「症状」を出す。
/// プレイヤー（Player）に付ける。
///
/// 【判定に使う量】
///   ・刺激度 irritation（0〜1）… 頭の位置の減光係数 K から。
///       K が irritationOnsetK を超えると目・のどの刺激が出始め、irritationFullK で最大。
///       （神 忠久の実験：刺激性の煙では K≒0.4〜0.5 [1/m] 付近から目を開けていられず歩行速度が急低下）
///   ・FED（CO による行動不能の累積量）… Purser / ISO 13571 の式
///       FED = Σ CO[ppm] × Δt[分] / 35000 。FED ≥ 0.3 で感受性の高い人が行動不能になりうる。
///   ・刺激曝露量 dose = Σ K × Δt [1/m·s] … 「濃い煙の中にどれだけいたか」
///
/// 【症状（段階）】
///   1: 目・のどの刺激  … 視界の周りが暗くなる、涙でにじむ、時々まばたき、咳、少し遅くなる
///   2: 強い刺激・息苦しさ … 視界がさらに狭く赤黒くなる、長いまばたき、咳が増える、大きく遅くなる
///   3: 行動不能       … 画面が暗転し、その時点で計測終了（ログに incapacitated が残る）
/// ※段階のしきい値は教育・実験用の目安で、医学的な判定ではない。
/// ※VR 酔いを防ぐため、カメラを揺らす演出は使っていない。
/// </summary>
public class SmokeExposure : MonoBehaviour, IEvacLogColumns
{
    [Header("参照")]
    public SmokeLayerController smoke;
    [Tooltip("頭（カメラ / CenterEyeAnchor）")]
    public Transform head;
    [Tooltip("移動速度を落とす対象（PC用の移動スクリプト）。VR 移動では未設定でよい")]
    public PlayerMovements movements;
    [Tooltip("視界の周りを暗くするビネットの板（カメラの子）")]
    public Renderer overlay;
    [Tooltip("まばたき・暗転用の黒い板（カメラの子）")]
    public Renderer blackout;
    [Tooltip("咳の音を鳴らす AudioSource（未設定なら自動で追加）")]
    public AudioSource coughSource;
    [Tooltip("咳の音（未設定なら簡易的な咳の音を自動生成）")]
    public AudioClip[] coughClips;

    [Header("刺激（減光係数 K による）")]
    [Tooltip("刺激が出始める K [1/m]")]
    public float irritationOnsetK = 0.15f;
    [Tooltip("刺激が最大になる K [1/m]")]
    public float irritationFullK = 0.6f;
    [Tooltip("刺激が強まる速さ（秒）")]
    public float riseTime = 0.8f;
    [Tooltip("煙から出たあと刺激が引く速さ（秒）")]
    public float recoverTime = 6f;

    [Header("段階のしきい値")]
    [Range(0f, 1f)] public float stage1Irritation = 0.2f;
    [Range(0f, 1f)] public float stage2Irritation = 0.6f;
    [Tooltip("段階2になる FED")]
    public float stage2FED = 0.1f;
    [Tooltip("行動不能になる FED（ISO 13571: 0.3）")]
    public float incapacitationFED = 0.3f;
    [Tooltip("行動不能になる刺激曝露量 Σ K·dt [1/m·s]（例: K=2 の煙に 40 秒）")]
    public float incapacitationDose = 80f;
    [Tooltip("曝露の進み方の倍率（1 = 計算どおり。演出として強めたいときに上げる。ログに残る）")]
    [Range(0.1f, 10f)] public float exposureGain = 1f;
    [Tooltip("段階3で計測を終了する")]
    public bool endSessionOnIncapacitation = true;

    [Header("移動速度の倍率")]
    [Range(0f, 1f)] public float speedStage1 = 0.85f;
    [Range(0f, 1f)] public float speedStage2 = 0.55f;

    [Header("見た目")]
    public Color vignetteColor = new Color(0.08f, 0.02f, 0.02f, 1f);
    [Range(0f, 1f)] public float vignetteStage1 = 0.45f;
    [Range(0f, 1f)] public float vignetteStage2 = 0.8f;
    [Tooltip("VR コントローラーを振動させる")]
    public bool haptics = true;

    // ---- 状態（ログ用に公開） ----
    public float Irritation { get; private set; }
    public float FED { get; private set; }
    public float Dose { get; private set; }
    public int Stage { get; private set; }
    public float HeadK { get; private set; }
    public float HeadCO { get; private set; }
    public bool InSmoke { get; private set; }
    public float TimeInSmoke { get; private set; }

    float baseWalk, baseFast, baseCrouch;
    float nextCough, nextBlink, blinkUntil, blinkLen;
    MaterialPropertyBlock mpb;
    TextMesh message;
    bool ended;

    void Start()
    {
        if (head == null && Camera.main != null) head = Camera.main.transform;
        if (smoke == null) smoke = FindAnyObjectByType<SmokeLayerController>();
        if (movements == null) movements = GetComponent<PlayerMovements>();
        if (movements != null) { baseWalk = movements.walkSpeed; baseFast = movements.fastSpeed; baseCrouch = movements.crouchSpeed; }
        if (coughSource == null)
        {
            coughSource = gameObject.AddComponent<AudioSource>();
            coughSource.spatialBlend = 0f;
            coughSource.playOnAwake = false;
        }
        if (coughClips == null || coughClips.Length == 0)
            coughClips = new[] { MakeCough(0), MakeCough(1), MakeCough(2) };
        mpb = new MaterialPropertyBlock();
        FitPlate(overlay, 1.15f);
        FitPlate(blackout, 1.6f);
        SetOverlay(0f);
        nextCough = Time.time + 2f;
        nextBlink = Time.time + 3f;
    }

    void Update()
    {
        if (smoke == null || head == null) return;
        var m = EvacuationSessionManager.Instance;
        bool running = m == null || m.State == EvacuationState.Running;
        float dt = Time.deltaTime;

        HeadK = smoke.ExtinctionAt(head.position.y);
        HeadCO = smoke.COAt(head.position.y);
        bool nowIn = smoke.UpperWeight(head.position.y) > 0.5f;
        if (nowIn != InSmoke) LogEvent(nowIn ? "smoke_enter" : "smoke_exit", $"k={HeadK:F2};layer={smoke.LayerHeightAboveFloor:F2}");
        InSmoke = nowIn;

        // 刺激度（なめらかに追従）
        float target = Mathf.Clamp01(Mathf.InverseLerp(irritationOnsetK, irritationFullK, HeadK));
        float tau = target > Irritation ? riseTime : recoverTime;
        Irritation = Mathf.MoveTowards(Irritation, target, dt / Mathf.Max(0.05f, tau));

        if (running && !ended)
        {
            FED += exposureGain * HeadCO * (dt / 60f) / 35000f;
            if (HeadK > irritationOnsetK) Dose += exposureGain * HeadK * dt;
            if (InSmoke) TimeInSmoke += dt;
        }

        // 段階
        int st = 0;
        if (Irritation >= stage1Irritation) st = 1;
        if (Irritation >= stage2Irritation || FED >= stage2FED) st = 2;
        if (FED >= incapacitationFED || Dose >= incapacitationDose) st = 3;
        if (ended) st = 3;
        if (st != Stage && Stage < 3)
        {
            LogEvent("symptom_stage", $"{st};irr={Irritation:F2};fed={FED:F3};dose={Dose:F1}");
            Stage = st;
        }

        ApplySpeed();
        UpdateCough();
        UpdateOverlay();

        if (Stage == 3 && !ended && running) Incapacitate();
    }

    void ApplySpeed()
    {
        if (movements == null) return;
        float f = Stage >= 3 ? 0f : Stage == 2 ? speedStage2 : Stage == 1 ? speedStage1 : 1f;
        movements.walkSpeed = baseWalk * f;
        movements.fastSpeed = baseFast * f;
        movements.crouchSpeed = baseCrouch * Mathf.Max(f, Stage >= 3 ? 0f : 0.8f); // しゃがんでいる方が楽
    }

    void UpdateCough()
    {
        if (Stage == 0 || Stage >= 3 || Time.time < nextCough) return;
        var clip = coughClips[Random.Range(0, coughClips.Length)];
        if (clip != null) coughSource.PlayOneShot(clip, Stage == 2 ? 1f : 0.7f);
        if (haptics) Haptic(Stage == 2 ? 0.6f : 0.35f, 0.25f);
        nextCough = Time.time + (Stage == 2 ? Random.Range(2.5f, 4.5f) : Random.Range(5f, 9f));
        LogEvent("cough", Stage.ToString());
    }

    void UpdateOverlay()
    {
        float a = 0f;
        if (Stage >= 1) a = Mathf.Lerp(vignetteStage1 * 0.6f, vignetteStage1, Irritation);
        if (Stage >= 2) a = Mathf.Lerp(vignetteStage1, vignetteStage2, Irritation);
        // 涙でにじむ（ゆらぎ）
        if (Stage >= 1) a += (Mathf.PerlinNoise(Time.time * 1.7f, 0.3f) - 0.5f) * 0.12f;

        // まばたき（目がしみて閉じる）
        if (Stage >= 1 && Time.time > nextBlink)
        {
            blinkLen = Stage == 2 ? Random.Range(0.4f, 0.8f) : Random.Range(0.15f, 0.3f);
            blinkUntil = Time.time + blinkLen;
            nextBlink = Time.time + (Stage == 2 ? Random.Range(2f, 4f) : Random.Range(4f, 8f));
        }
        float blink = 0f;
        if (Time.time < blinkUntil)
        {
            float u = 1f - (blinkUntil - Time.time) / Mathf.Max(0.01f, blinkLen);
            blink = Mathf.Sin(u * Mathf.PI);
        }
        if (Stage >= 3) { a = 1f; blink = 1f; }
        SetOverlay(Mathf.Clamp01(a), blink * (Stage >= 2 ? 1f : 0.85f));
    }

    void SetOverlay(float vignette, float black = 0f)
    {
        SetPlate(overlay, vignetteColor, vignette);
        SetPlate(blackout, Color.black, black);
    }

    void SetPlate(Renderer r, Color c, float a)
    {
        if (r == null) return;
        r.enabled = a > 0.001f;
        c.a = a;
        r.GetPropertyBlock(mpb);
        mpb.SetColor("_BaseColor", c);
        mpb.SetColor("_Color", c);
        r.SetPropertyBlock(mpb);
    }

    // 板をカメラの視野ぴったり（少し大きめ）に合わせる
    void FitPlate(Renderer r, float margin)
    {
        if (r == null || head == null) return;
        var cam = head.GetComponent<Camera>();
        if (cam == null) return;
        float d = Mathf.Max(cam.nearClipPlane * 1.5f, r.transform.localPosition.z);
        float h = 2f * d * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * margin;
        float w = h * Mathf.Max(1f, cam.aspect);
        r.transform.localPosition = new Vector3(0f, 0f, d);
        r.transform.localRotation = Quaternion.identity;
        r.transform.localScale = new Vector3(w, h, 1f);
    }

    void Incapacitate()
    {
        ended = true;
        LogEvent("incapacitated", $"fed={FED:F3};dose={Dose:F1};t_in_smoke={TimeInSmoke:F1}");
        if (haptics) Haptic(1f, 0.8f);
        ShowMessage("煙を吸い込み、動けなくなりました");
        var m = EvacuationSessionManager.Instance;
        if (endSessionOnIncapacitation && m != null && m.State == EvacuationState.Running)
            m.timeLimit = Mathf.Max(0.01f, m.ElapsedTime); // 次のフレームで「時間切れ」として終了させる
    }

    void ShowMessage(string text)
    {
        if (head == null) return;
        if (message == null)
        {
            var go = new GameObject("IncapacitatedMessage");
            go.transform.SetParent(head, false);
            go.transform.localPosition = new Vector3(0f, 0f, 0.25f); // 暗転用の板より手前に置く
            message = go.AddComponent<TextMesh>();
            message.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            message.anchor = TextAnchor.MiddleCenter;
            message.alignment = TextAlignment.Center;
            message.characterSize = 0.004f;
            message.fontSize = 64;
            message.color = new Color(1f, 0.85f, 0.85f);
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null && message.font != null) mr.sharedMaterial = message.font.material;
        }
        message.text = text;
    }

    void Haptic(float amp, float dur)
    {
        var dev = new System.Collections.Generic.List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Controller, dev);
        foreach (var d in dev) d.SendHapticImpulse(0u, amp, dur);
    }

    void LogEvent(string ev, string detail)
    {
        if (EvacuationTrajectoryLogger.Instance != null) EvacuationTrajectoryLogger.Instance.LogEvent(ev, "player", detail);
    }

    // ---- ログの追加列 ----
    public string LogHeader => "smoke_layer_m,k_head,co_head_ppm,in_smoke,irritation,fed,dose,stage,head_above_floor_m";
    public string LogValues
    {
        get
        {
            var ic = CultureInfo.InvariantCulture;
            float layer = smoke != null ? smoke.LayerHeightAboveFloor : -1f;
            float hf = (head != null && smoke != null) ? head.position.y - smoke.floorY : -1f;
            return string.Join(",",
                layer.ToString("F3", ic), HeadK.ToString("F3", ic), HeadCO.ToString("F0", ic), InSmoke ? "1" : "0",
                Irritation.ToString("F3", ic), FED.ToString("F4", ic), Dose.ToString("F2", ic), Stage.ToString(ic), hf.ToString("F3", ic));
        }
    }

    // ---- 簡易的な咳の音（ノイズを整形して作る） ----
    static AudioClip MakeCough(int seed)
    {
        const int rate = 44100;
        var rnd = new System.Random(1234 + seed);
        int bursts = 1 + seed % 2 + 1;
        float total = 0.12f + bursts * 0.28f;
        int n = (int)(rate * total);
        var data = new float[n];
        float lp = 0f;
        for (int b = 0; b < bursts; b++)
        {
            float start = 0.05f + b * 0.28f + (float)rnd.NextDouble() * 0.04f;
            float len = 0.18f + (float)rnd.NextDouble() * 0.06f;
            float f0 = 180f + (float)rnd.NextDouble() * 80f;
            for (int i = (int)(start * rate); i < Mathf.Min(n, (int)((start + len) * rate)); i++)
            {
                float t = i / (float)rate - start;
                float env = Mathf.Min(1f, t / 0.012f) * Mathf.Exp(-t / (len * 0.35f));
                float noise = (float)(rnd.NextDouble() * 2 - 1);
                lp += 0.25f * (noise - lp); // こもった音にするローパス
                float tone = Mathf.Sin(2f * Mathf.PI * f0 * t) * 0.25f;
                data[i] += (lp * 1.6f + tone) * env * (b == 0 ? 0.9f : 0.7f);
            }
        }
        var clip = AudioClip.Create("Cough" + seed, n, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
