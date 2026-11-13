using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// EvacuationSessionManager のイベントに合わせて、画面にメッセージを出す。
///   スタート → 「脱出開始！」（数秒で消える）
///   時間切れ → 「時間切れ…」
/// ※ゴール時の表示は別メンバーの担当なので、ここでは出さない。
///   ゴールした瞬間に、表示中のメッセージは消す（重ならないように）。
/// 空の GameObject に付けるだけで、表示用の Canvas を自動で作る。
/// シーンに EvacuationSessionManager が必要。
/// </summary>
public class EvacuationMessageUI : MonoBehaviour
{
    public enum DisplayMode
    {
        ScreenOverlay,      // PC画面に表示（キーボード操作のテスト用）
        VRInFrontOfCamera   // VRゴーグルの目の前に表示（Questで遊ぶとき用）
    }

    [Header("表示方法")]
    public DisplayMode displayMode = DisplayMode.ScreenOverlay;
    [Tooltip("VRモードで追従するカメラ。未設定なら Main Camera を使う")]
    public Transform vrCamera;
    [Tooltip("VRモードで目の前に出す距離(m)")]
    public float vrDistance = 2f;

    [Header("メッセージ")]
    public string startMessage = "脱出開始！";
    public string timeoutMessage = "時間切れ…";

    [Header("見た目")]
    public int fontSize = 72;
    public Color textColor = Color.white;
    public Color backgroundColor = new Color(0f, 0f, 0f, 0.55f);
    [Tooltip("スタートのメッセージを表示しておく秒数")]
    public float showSeconds = 2f;
    [Tooltip("消えるときのフェード秒数")]
    public float fadeSeconds = 0.5f;
    [Tooltip("時間切れのメッセージは消さずに残す")]
    public bool keepTimeoutMessage = true;

    Canvas canvas;
    CanvasGroup group;
    Text label;
    Coroutine routine;

    void Awake()
    {
        BuildUI();
        group.alpha = 0f;
    }

    void Start()
    {
        var manager = EvacuationSessionManager.Instance;
        if (manager == null)
        {
            Debug.LogError("[MessageUI] シーンに EvacuationSessionManager がありません。");
            return;
        }

        manager.onSessionStarted.AddListener(() => Show(startMessage, false));
        manager.onSessionFinished.AddListener(_ => Hide());
        manager.onSessionTimedOut.AddListener(() => Show(timeoutMessage, keepTimeoutMessage));

        if (displayMode == DisplayMode.VRInFrontOfCamera && vrCamera == null && Camera.main != null)
            vrCamera = Camera.main.transform;
    }

    void LateUpdate()
    {
        // VRモードでは常にカメラの正面に置く
        if (displayMode != DisplayMode.VRInFrontOfCamera || vrCamera == null) return;
        canvas.transform.position = vrCamera.position + vrCamera.forward * vrDistance;
        canvas.transform.rotation = Quaternion.LookRotation(vrCamera.forward, Vector3.up);
    }

    /// <summary>任意のメッセージを表示する（ほかのスクリプトからも呼べる）</summary>
    public void Show(string message, bool keep)
    {
        if (routine != null) StopCoroutine(routine);
        routine = StartCoroutine(ShowRoutine(message, keep));
    }

    /// <summary>表示中のメッセージをすぐ消す</summary>
    public void Hide()
    {
        if (routine != null) StopCoroutine(routine);
        routine = null;
        group.alpha = 0f;
    }

    IEnumerator ShowRoutine(string message, bool keep)
    {
        label.text = message;
        group.alpha = 1f;
        if (keep) yield break;

        yield return new WaitForSeconds(showSeconds);

        float t = 0f;
        while (t < fadeSeconds)
        {
            t += Time.deltaTime;
            group.alpha = 1f - t / fadeSeconds;
            yield return null;
        }
        group.alpha = 0f;
    }

    void BuildUI()
    {
        // Canvas
        var canvasGo = new GameObject("EvacuationMessageCanvas");
        canvasGo.transform.SetParent(transform, false);
        canvas = canvasGo.AddComponent<Canvas>();
        group = canvasGo.AddComponent<CanvasGroup>();
        group.interactable = false;
        group.blocksRaycasts = false;

        var canvasRect = canvasGo.GetComponent<RectTransform>();
        if (displayMode == DisplayMode.ScreenOverlay)
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
        }
        else
        {
            canvas.renderMode = RenderMode.WorldSpace;
            canvasRect.sizeDelta = new Vector2(1200, 400);
            canvasRect.localScale = Vector3.one * 0.0015f; // 約1.8m × 0.6m
        }

        // 背景の帯
        var bgGo = new GameObject("Background");
        bgGo.transform.SetParent(canvasGo.transform, false);
        var bg = bgGo.AddComponent<Image>();
        bg.color = backgroundColor;
        bg.raycastTarget = false;
        var bgRect = bgGo.GetComponent<RectTransform>();
        bgRect.anchorMin = bgRect.anchorMax = new Vector2(0.5f, 0.5f);
        bgRect.sizeDelta = new Vector2(1100, 260);

        // 文字（日本語が出るよう、OSフォントにフォールバックする標準フォントを使う）
        var textGo = new GameObject("Message");
        textGo.transform.SetParent(bgGo.transform, false);
        label = textGo.AddComponent<Text>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontSize = fontSize;
        label.fontStyle = FontStyle.Bold;
        label.color = textColor;
        label.alignment = TextAnchor.MiddleCenter;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        label.raycastTarget = false;
        var textRect = textGo.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = textRect.offsetMax = Vector2.zero;
    }
}
