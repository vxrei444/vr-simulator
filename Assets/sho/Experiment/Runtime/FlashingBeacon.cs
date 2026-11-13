using UnityEngine;

/// <summary>
/// 誘導灯・看板・矢印の「光り方」を制御する共通コンポーネント。
/// 点灯パターン・点滅の速さ・色・明るさを Inspector で変えられる。
///   Steady      … 常時点灯（光る看板）
///   Blink       … 一定周期で点滅（点滅灯）
///   Pulse       … ふわっと明滅（サイン波）
///   Strobe      … 短いフラッシュ（ストロボ）
///   DoubleFlash … 2回連続フラッシュ
/// 対象の Renderer の色（_BaseColor / _EmissionColor）と、任意の Light を同時に変える。
///
/// ※光過敏性発作の安全上、点滅は 3Hz 以下を推奨（WCAG 2.x / ITU-R BT.1702 の基準）。
///   allowOver3Hz を有効にしない限り 3Hz で頭打ちにする。
/// </summary>
[DisallowMultipleComponent]
public class FlashingBeacon : MonoBehaviour
{
    public enum Pattern { Steady, Blink, Pulse, Strobe, DoubleFlash }

    [Header("点灯パターン")]
    public Pattern pattern = Pattern.Blink;

    [Tooltip("点滅の速さ（1秒あたりの回数, Hz）")]
    [Range(0.1f, 10f)] public float frequencyHz = 1.0f;

    [Tooltip("Blink のとき、1周期のうち点いている割合（0.5 = 半分点灯）")]
    [Range(0.05f, 0.95f)] public float dutyCycle = 0.5f;

    [Tooltip("ほかの灯とタイミングをずらす（0〜1周期）。順番に光らせたいときに使う")]
    [Range(0f, 1f)] public float phaseOffset = 0f;

    [Tooltip("3Hz を超える点滅を許可する（光過敏性への配慮のため通常はオフ）")]
    public bool allowOver3Hz = false;

    [Header("色・明るさ")]
    [ColorUsage(false, true)] public Color color = new Color(0.1f, 1f, 0.3f);
    [Tooltip("点灯時の明るさ倍率（看板の発光の強さ）")]
    [Range(0f, 5f)] public float brightness = 1.5f;
    [Tooltip("消灯時に残る明るさ（0 = 完全に消える）")]
    [Range(0f, 1f)] public float offLevel = 0.05f;

    [Header("対象")]
    [Tooltip("色を変える Renderer（未設定なら自分と子の Renderer 全部）")]
    public Renderer[] targetRenderers;
    [Tooltip("一緒に明滅させるライト（任意）")]
    public Light targetLight;
    [Tooltip("ライトの最大の明るさ")]
    public float lightIntensity = 2f;

    [Header("タイミング")]
    [Tooltip("計測（スタート）が始まるまでは光らせない")]
    public bool waitForSessionStart = false;

    /// <summary>現在の明るさ（0〜1）。ログや他スクリプトから参照できる</summary>
    public float CurrentLevel { get; private set; }

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");
    static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");
    MaterialPropertyBlock mpb;

    public float EffectiveFrequency => allowOver3Hz ? frequencyHz : Mathf.Min(frequencyHz, 3f);

    void Awake()
    {
        mpb = new MaterialPropertyBlock();
        if (targetRenderers == null || targetRenderers.Length == 0)
            targetRenderers = GetComponentsInChildren<Renderer>();
    }

    void OnValidate()
    {
        if (!allowOver3Hz && frequencyHz > 3f)
            Debug.LogWarning($"[FlashingBeacon] {name}: 3Hz を超える点滅は光過敏性発作の危険があるため 3Hz に制限されます。");
    }

    void Update()
    {
        float level;
        var m = EvacuationSessionManager.Instance;
        if (waitForSessionStart && (m == null || m.State == EvacuationState.Waiting))
            level = 0f;
        else
            level = Evaluate(Time.time);
        Apply(level);
    }

    /// <summary>時刻 t における明るさ 0〜1</summary>
    public float Evaluate(float t)
    {
        if (pattern == Pattern.Steady) return 1f;
        float f = Mathf.Max(0.01f, EffectiveFrequency);
        float ph = Mathf.Repeat(t * f + phaseOffset, 1f);
        switch (pattern)
        {
            case Pattern.Blink: return ph < dutyCycle ? 1f : 0f;
            case Pattern.Pulse: return 0.5f - 0.5f * Mathf.Cos(ph * Mathf.PI * 2f);
            case Pattern.Strobe: return ph < 0.08f ? 1f : 0f;
            case Pattern.DoubleFlash: return (ph < 0.08f || (ph > 0.16f && ph < 0.24f)) ? 1f : 0f;
        }
        return 1f;
    }

    void Apply(float level)
    {
        CurrentLevel = level;
        float l = Mathf.Lerp(offLevel, 1f, level);
        Color c = color * (l * brightness);
        Color baseC = Color.Lerp(color * offLevel, color, level);
        baseC.a = 1f;

        if (targetRenderers != null)
        {
            foreach (var r in targetRenderers)
            {
                if (r == null) continue;
                r.GetPropertyBlock(mpb);
                mpb.SetColor(BaseColorId, baseC);
                mpb.SetColor(ColorId, baseC);
                mpb.SetColor(EmissionId, c);
                r.SetPropertyBlock(mpb);
            }
        }
        if (targetLight != null)
        {
            targetLight.color = color;
            targetLight.intensity = lightIntensity * l;
        }
    }

    /// <summary>ログに残す設定文字列</summary>
    public string Describe() =>
        $"{pattern};{EffectiveFrequency:F2}Hz;duty={dutyCycle:F2};bright={brightness:F2};color=#{ColorUtility.ToHtmlStringRGB(color)}";
}
