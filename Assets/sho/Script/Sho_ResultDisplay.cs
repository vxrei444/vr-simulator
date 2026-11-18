using TMPro;
using UnityEngine;

/// <summary>
/// ResultScene で脱出タイムを表示する。ResultScene の GameClear（TextMeshPro の文字）に付ける。
///   ・タイム表示用の文字が未設定なら、GameClear の下に自動で作る（フォント・マテリアルは GameClear と同じ）
///   ・ゴールできなかった（時間切れ・行動不能）ときは、見出しを failTitle に変えて色も変える
/// 値は Sho_GoToResultScene（ゴール時に保存）から読む。
/// ※標準フォント（LiberationSans）は日本語が出ないので、表示文は英語にしている。
///   日本語にしたい場合は、日本語の TextMeshPro フォントを用意して文面を変える。
/// </summary>
[RequireComponent(typeof(TMP_Text))]
public class Sho_ResultDisplay : MonoBehaviour
{
    [Header("表示する文字（未設定なら自動で作る）")]
    public TMP_Text timeText;

    [Header("文面")]
    public string clearTitle = "Game Clear!";
    public string failTitle = "Time Up...";
    [Tooltip("{0} にタイムが入る")]
    public string timeFormat = "Escape Time  {0}";
    public string failTimeFormat = "Survived  {0}";

    [Header("見た目")]
    public float timeFontSize = 90f;
    public Color timeColor = new Color(1f, 1f, 1f, 1f);
    public Color failTitleColor = new Color(0.7f, 0.75f, 0.85f, 1f);
    [Tooltip("見出しの下、どれだけ離して置くか（Canvas の単位）")]
    public float gapBelowTitle = 40f;

    [Header("登場の演出")]
    public bool animate = true;
    public float popSeconds = 0.6f;

    TMP_Text title;
    float t0;
    Vector3 titleScale, timeScale;

    void Start()
    {
        title = GetComponent<TMP_Text>();
        if (timeText == null) timeText = CreateTimeText();

        bool cleared = Sho_GoToResultScene.LastReachedGoal;
        float sec = Sho_GoToResultScene.LastEscapeTime;
        if (!cleared)
        {
            title.text = failTitle;
            title.enableVertexGradient = false;
            title.color = failTitleColor;
        }
        else if (!string.IsNullOrEmpty(clearTitle)) title.text = clearTitle;

        timeText.text = string.Format(cleared ? timeFormat : failTimeFormat, FormatTime(sec));

        titleScale = title.transform.localScale;
        timeScale = timeText.transform.localScale;
        t0 = Time.time;
        if (animate) { title.transform.localScale = Vector3.zero; timeText.transform.localScale = Vector3.zero; }
    }

    void Update()
    {
        if (!animate) return;
        float u = (Time.time - t0) / Mathf.Max(0.05f, popSeconds);
        title.transform.localScale = titleScale * Pop(u);
        timeText.transform.localScale = timeScale * Pop(u - 0.4f); // 見出しの少し後に出る
        if (u > 1.5f) animate = false;
    }

    // 少し大きく飛び出してから落ち着く
    static float Pop(float u)
    {
        if (u <= 0f) return 0f;
        if (u >= 1f) return 1f;
        const float s = 1.70158f;
        float x = u - 1f;
        return 1f + x * x * ((s + 1f) * x + s);
    }

    TMP_Text CreateTimeText()
    {
        var go = new GameObject("EscapeTime", typeof(RectTransform));
        go.transform.SetParent(transform.parent, false);
        go.layer = gameObject.layer;
        var t = go.AddComponent<TextMeshProUGUI>();
        var src = GetComponent<TMP_Text>();
        t.font = src.font;
        t.fontSharedMaterial = src.fontSharedMaterial;   // 縁取り・影などもそろえる
        t.fontSize = timeFontSize;
        t.fontStyle = FontStyles.Bold;
        t.color = timeColor;
        t.alignment = TextAlignmentOptions.Center;
        t.textWrappingMode = TextWrappingModes.NoWrap;

        var srcRt = (RectTransform)transform;
        var rt = (RectTransform)go.transform;
        rt.anchorMin = srcRt.anchorMin; rt.anchorMax = srcRt.anchorMax; rt.pivot = new Vector2(0.5f, 1f);
        rt.sizeDelta = new Vector2(Mathf.Max(srcRt.sizeDelta.x, 1200f), timeFontSize * 1.5f);
        float titleBottom = srcRt.anchoredPosition.y - srcRt.sizeDelta.y * srcRt.pivot.y;
        rt.anchoredPosition = new Vector2(srcRt.anchoredPosition.x, titleBottom - gapBelowTitle);
        return t;
    }

    static string FormatTime(float sec)
    {
        if (sec < 0f) sec = 0f;
        int m = (int)(sec / 60f);
        float s = sec - m * 60f;
        return $"{m:00}:{s:00.00}";
    }
}
