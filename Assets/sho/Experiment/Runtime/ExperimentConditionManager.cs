using System;
using System.Collections;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// 対照実験の「条件」を1か所で切り替える。
///   誘導条件（A: なし / B: 床矢印 / C: 光る看板 / D: 点滅灯 / E: 動く矢印）
///   煙条件（なし / ゆっくり / 標準 / 速い）
/// 選んだ条件のオブジェクトだけを表示し、ほかは非表示にする。
/// 条件名は EvacuationSessionManager.conditionId に自動で書き込まれ、ログのファイル名にも入る。
///
/// エディタでの確認用：スタート前に キーボード 1〜5 で誘導条件、Q/W/E/R で煙条件を切り替えられる。
/// </summary>
public class ExperimentConditionManager : MonoBehaviour
{
    public enum Guidance { A_None, B_FloorArrow, C_GlowSign, D_Flashing, E_FlowingArrow }
    public enum SmokePreset { Off, Slow, Medium, Fast }

    [Serializable]
    public class GuidanceGroup
    {
        public Guidance guidance;
        [Tooltip("この条件のときだけ表示するオブジェクト")]
        public GameObject[] objects;
    }

    [Header("今回の実験条件")]
    public Guidance guidance = Guidance.D_Flashing;
    public SmokePreset smoke = SmokePreset.Medium;

    [Header("参加者")]
    [Tooltip("空でなければ EvacuationSessionManager.participantId に書き込む")]
    public string participantId = "P01";

    [Header("条件ごとのオブジェクト")]
    public GuidanceGroup[] groups;

    [Header("参照")]
    public SmokeLayerController smokeController;

    [Header("エディタ確認用")]
    public bool enableKeyboardSwitch = true;
    public bool showOverlay = true;

    public string ConditionId => $"{GuidanceCode(guidance)}_{SmokeCode(smoke)}";

    void Awake() => ApplyAll();

    void Start()
    {
        var m = EvacuationSessionManager.Instance;
        if (m != null) m.onSessionStarted.AddListener(() => StartCoroutine(LogConfigNextFrame()));
    }

    IEnumerator LogConfigNextFrame()
    {
        yield return null; // ロガーの記録開始を待つ
        var log = EvacuationTrajectoryLogger.Instance;
        if (log == null) yield break;
        log.LogEvent("config_guidance", guidance.ToString(), DescribeGuidance());
        if (smokeController != null) log.LogEvent("config_smoke", smoke.ToString(), smokeController.Describe());
    }

    void Update()
    {
#if ENABLE_INPUT_SYSTEM
        if (!enableKeyboardSwitch || Keyboard.current == null) return;
        var m = EvacuationSessionManager.Instance;
        if (m != null && m.State != EvacuationState.Waiting) return; // 計測中は変えない
        var k = Keyboard.current;
        Guidance? g = null;
        if (k.digit1Key.wasPressedThisFrame) g = Guidance.A_None;
        if (k.digit2Key.wasPressedThisFrame) g = Guidance.B_FloorArrow;
        if (k.digit3Key.wasPressedThisFrame) g = Guidance.C_GlowSign;
        if (k.digit4Key.wasPressedThisFrame) g = Guidance.D_Flashing;
        if (k.digit5Key.wasPressedThisFrame) g = Guidance.E_FlowingArrow;
        SmokePreset? s = null;
        if (k.qKey.wasPressedThisFrame) s = SmokePreset.Off;
        if (k.wKey.wasPressedThisFrame && k.leftAltKey.isPressed) s = SmokePreset.Slow;   // W は移動に使うので Alt+W
        if (k.eKey.wasPressedThisFrame) s = SmokePreset.Medium;
        if (k.rKey.wasPressedThisFrame && k.leftAltKey.isPressed) s = SmokePreset.Fast;   // R はリスタートと重なるので Alt+R
        if (g.HasValue) { guidance = g.Value; ApplyAll(); }
        if (s.HasValue) { smoke = s.Value; ApplyAll(); }
#endif
    }

    [ContextMenu("Apply Conditions")]
    public void ApplyAll()
    {
        if (groups != null)
        {
            foreach (var grp in groups)
            {
                if (grp?.objects == null) continue;
                bool on = grp.guidance == guidance;
                foreach (var o in grp.objects) if (o != null) o.SetActive(on);
            }
        }
        if (smokeController != null) smokeController.ApplyPreset(smoke);

        var m = EvacuationSessionManager.Instance != null ? EvacuationSessionManager.Instance : FindAnyObjectByType<EvacuationSessionManager>();
        if (m != null)
        {
            m.conditionId = ConditionId;
            if (!string.IsNullOrEmpty(participantId)) m.participantId = participantId;
        }
    }

    string DescribeGuidance()
    {
        if (groups == null) return "";
        foreach (var grp in groups)
        {
            if (grp.guidance != guidance || grp.objects == null) continue;
            foreach (var o in grp.objects)
            {
                if (o == null) continue;
                var b = o.GetComponentInChildren<FlashingBeacon>();
                if (b != null) return b.Describe();
                var f = o.GetComponentInChildren<FlowingArrowLights>();
                if (f != null) return f.Describe();
            }
        }
        return "";
    }

    static string GuidanceCode(Guidance g) => g.ToString().Split('_')[0] + "-" + g.ToString().Substring(2);
    static string SmokeCode(SmokePreset s) => "smoke" + s;

    void OnGUI()
    {
        if (!showOverlay || !Application.isEditor) return;
        var m = EvacuationSessionManager.Instance;
        string state = m != null ? m.State.ToString() : "-";
        string t = m != null ? m.ElapsedTime.ToString("F1") : "-";
        string smokeInfo = smokeController != null
            ? $"煙 {smokeController.CurrentFloorName} 煙層の高さ {smokeController.LayerY:F2} / 減光係数 {smokeController.ExtinctionUpper:F2} 1/m / 見通し {smokeController.VisibilityUpper:F1} m"
            : "";
        GUI.Label(new Rect(10, 10, 900, 22), $"条件 {ConditionId}  状態 {state}  経過 {t}s   [1-5]誘導 [Q]煙なし [Alt+W]遅 [E]標準 [Alt+R]速");
        GUI.Label(new Rect(10, 30, 900, 22), smokeInfo);
    }
}
