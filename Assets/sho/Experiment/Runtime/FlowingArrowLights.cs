using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 「動く矢印」：経路に沿って並べた矢印（ライト）が、出口の方向へ順番に光って流れる。
/// waypoints（スタート側→出口側）の順に、spacing 間隔で矢印を自動配置する。
/// Inspector で 流れる速さ・光の帯の長さ・色・明るさ を変えられる。
/// 配置し直すときは、コンポーネント右上の ⋮ → 「Rebuild Segments」。
/// </summary>
public class FlowingArrowLights : MonoBehaviour
{
    [Header("経路（スタート側 → 出口側の順）")]
    public Transform[] waypoints;
    [Tooltip("矢印の間隔(m)")]
    public float spacing = 0.6f;
    [Tooltip("床からの高さ(m)")]
    public float heightOffset = 0.02f;

    [Header("矢印の見た目")]
    [Tooltip("矢印1個の材質（矢印テクスチャ付き）")]
    public Material segmentMaterial;
    public Vector2 segmentSize = new Vector2(0.45f, 0.45f);

    [Header("光の流れ")]
    [Tooltip("光が流れる速さ(m/s)。人の歩行(約1.2m/s)より少し速いと追いかけやすい")]
    [Range(0.2f, 10f)] public float flowSpeed = 2.5f;
    [Tooltip("光の帯の長さ(m)")]
    [Range(0.2f, 10f)] public float bandLength = 1.8f;
    [Tooltip("帯と帯の間隔(m)。0 なら経路全体で1本だけ流れる")]
    public float bandRepeat = 6f;
    [ColorUsage(false, true)] public Color color = new Color(0.1f, 1f, 0.3f);
    [Range(0f, 5f)] public float brightness = 1.5f;
    [Range(0f, 1f)] public float offLevel = 0.08f;

    [Header("タイミング")]
    public bool waitForSessionStart = false;

    readonly List<Renderer> segs = new List<Renderer>();
    readonly List<float> segDist = new List<float>();
    MaterialPropertyBlock mpb;
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");
    static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

    void Awake()
    {
        mpb = new MaterialPropertyBlock();
        CollectExisting();
        if (segs.Count == 0) Rebuild();
    }

    void CollectExisting()
    {
        segs.Clear(); segDist.Clear();
        foreach (Transform c in transform)
        {
            if (!c.name.StartsWith("FlowSeg_")) continue;
            var r = c.GetComponent<Renderer>();
            if (r == null) continue;
            float d;
            if (!float.TryParse(c.name.Substring(8), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out d)) d = 0f;
            segs.Add(r); segDist.Add(d);
        }
    }

    [ContextMenu("Rebuild Segments")]
    public void Rebuild()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var c = transform.GetChild(i);
            if (!c.name.StartsWith("FlowSeg_")) continue;
            if (Application.isPlaying) Destroy(c.gameObject); else DestroyImmediate(c.gameObject);
        }
        segs.Clear(); segDist.Clear();
        if (waypoints == null || waypoints.Length < 2) return;

        float total = 0f;
        for (int w = 0; w < waypoints.Length - 1; w++)
        {
            if (waypoints[w] == null || waypoints[w + 1] == null) continue;
            Vector3 a = waypoints[w].position, b = waypoints[w + 1].position;
            Vector3 dir = b - a; dir.y = 0f;
            float len = dir.magnitude;
            if (len < 1e-3f) continue;
            dir /= len;
            for (float s = (w == 0 ? 0f : spacing * 0.5f); s <= len; s += spacing)
            {
                Vector3 p = a + dir * s; p.y = a.y + heightOffset;
                float dist = total + s;
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                q.name = "FlowSeg_" + dist.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                var col = q.GetComponent<Collider>();
                if (Application.isPlaying) Destroy(col); else DestroyImmediate(col);
                q.transform.SetParent(transform, true);
                q.transform.position = p;
                // Quad は -Z を向いて表示される。床に寝かせ、テクスチャの上方向(+Y)を進行方向に合わせる
                q.transform.rotation = Quaternion.LookRotation(Vector3.down, dir);
                q.transform.localScale = new Vector3(segmentSize.x, segmentSize.y, 1f);
                var r = q.GetComponent<Renderer>();
                if (segmentMaterial != null) r.sharedMaterial = segmentMaterial;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                segs.Add(r); segDist.Add(dist);
            }
            total += len;
        }
    }

    void Update()
    {
        var m = EvacuationSessionManager.Instance;
        bool off = waitForSessionStart && (m == null || m.State == EvacuationState.Waiting);
        float head = Time.time * flowSpeed;
        for (int i = 0; i < segs.Count; i++)
        {
            var r = segs[i];
            if (r == null) continue;
            float level = 0f;
            if (!off)
            {
                // 帯の先頭からどれだけ後ろか（0〜bandLength で光る、先頭が一番明るい）
                float behind = head - segDist[i];
                if (bandRepeat > 0f) behind = Mathf.Repeat(behind, bandRepeat);
                if (behind >= 0f && behind <= bandLength) level = 1f - behind / bandLength;
            }
            float l = Mathf.Lerp(offLevel, 1f, level);
            Color baseC = Color.Lerp(color * offLevel, color, level); baseC.a = 1f;
            r.GetPropertyBlock(mpb);
            mpb.SetColor(BaseColorId, baseC);
            mpb.SetColor(ColorId, baseC);
            mpb.SetColor(EmissionId, color * (l * brightness));
            r.SetPropertyBlock(mpb);
        }
    }

    public string Describe() =>
        $"Flow;speed={flowSpeed:F2}m/s;band={bandLength:F2}m;repeat={bandRepeat:F1}m;bright={brightness:F2};color=#{ColorUtility.ToHtmlStringRGB(color)}";

    void OnDrawGizmos()
    {
        if (waypoints == null) return;
        Gizmos.color = color;
        for (int i = 0; i < waypoints.Length - 1; i++)
            if (waypoints[i] != null && waypoints[i + 1] != null)
                Gizmos.DrawLine(waypoints[i].position, waypoints[i + 1].position);
    }
}
