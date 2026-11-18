#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 【taiki】今開いているシーンの「間取り」を、Python 分析用の JSON に書き出すエディタ機能。
/// メニュー: Tools > taiki > 間取りを書き出す（分析用JSON）
///
/// しくみ：シーンを上から格子状（既定 0.25m 間隔）に調べ、各地点で
///   ・真上から下へレイを何度も飛ばし、上向きの面（床・階段・机の上など）をすべて見つける
///   ・その面の上に人が立てる空間（高さ agentHeight）があれば「歩ける」とする
/// → 複数階・階段にも対応。Python 側でこれをつないで最短経路や「ゴールまでの道のり」を計算する。
/// さらに 炎（FireVisual / taiki_MapMarker）・障害物・スタート（StartZone）・ゴール（GoalZone）の位置も書き出す。
///
/// 注意：コライダーの無いオブジェクトは見えない（壁にも床にもならない）。
///       Player（タグ Player）とトリガーは無視する。
/// </summary>
public class taiki_LayoutExporter : EditorWindow
{
    private float taiki_cell = 0.25f;
    private float taiki_agentHeight = 1.6f;
    private float taiki_agentRadius = 0.2f;
    private float taiki_stepHeight = 0.45f;       // これ以下の段差は歩いて越えられる
    private float taiki_maxSlopeNormalY = 0.7f;   // 法線のy成分がこれ以上なら「上向きの面」
    private string taiki_floorText = "";          // 例 "1F:-1:3.5, 2F:3.5:7.5"（空なら Python 側で自動判定）
    private bool taiki_useCustomBounds = false;
    private Bounds taiki_bounds = new Bounds(Vector3.zero, new Vector3(60, 20, 60));

    [MenuItem("Tools/taiki/間取りを書き出す（分析用JSON）")]
    private static void taiki_Open()
    {
        var taiki_w = GetWindow<taiki_LayoutExporter>("taiki 間取り書き出し");
        taiki_w.minSize = new Vector2(380, 330);
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("今開いているシーンの間取りを JSON に書き出します", EditorStyles.boldLabel);
        EditorGUILayout.Space();
        taiki_cell = EditorGUILayout.Slider("格子の間隔 (m)", taiki_cell, 0.1f, 1f);
        taiki_agentHeight = EditorGUILayout.FloatField("人の高さ (m)", taiki_agentHeight);
        taiki_agentRadius = EditorGUILayout.FloatField("人の半径 (m)", taiki_agentRadius);
        taiki_stepHeight = EditorGUILayout.FloatField("越えられる段差 (m)", taiki_stepHeight);
        EditorGUILayout.HelpBox("実験場（講義棟）は約3.5倍の縮尺なので、目安は 格子 1.0 / 人の高さ 5 / 半径 0.7 / 段差 1.5。", MessageType.Info);
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("階の高さ範囲（空欄なら自動判定）");
        taiki_floorText = EditorGUILayout.TextField("例 1F:-1:3.5, 2F:3.5:7.5", taiki_floorText);
        EditorGUILayout.Space();
        taiki_useCustomBounds = EditorGUILayout.Toggle("調べる範囲を手動で指定", taiki_useCustomBounds);
        if (taiki_useCustomBounds)
        {
            taiki_bounds.center = EditorGUILayout.Vector3Field("中心", taiki_bounds.center);
            taiki_bounds.size = EditorGUILayout.Vector3Field("大きさ", taiki_bounds.size);
        }
        else
        {
            EditorGUILayout.HelpBox("範囲はシーン内のコライダー全体から自動で決めます。\n遠くに関係ない物があるときは、手動指定に切り替えて下のボタンを使ってください。", MessageType.None);
        }
        if (GUILayout.Button("Hierarchyで選択中のオブジェクトの範囲を使う"))
        {
            taiki_SetBoundsFromSelection();
        }
        Bounds taiki_preview = taiki_useCustomBounds ? taiki_bounds : taiki_SceneBounds();
        int taiki_cols = Mathf.CeilToInt(taiki_preview.size.x / taiki_cell) * Mathf.CeilToInt(taiki_preview.size.z / taiki_cell);
        EditorGUILayout.LabelField($"調べる地点の数：約 {taiki_cols:N0}（多いほど時間がかかる。50万を超えるなら格子を粗く）");
        EditorGUILayout.Space();
        if (GUILayout.Button("書き出す", GUILayout.Height(32))) taiki_Export();
    }

    private void taiki_Export()
    {
        Scene taiki_scene = SceneManager.GetActiveScene();
        Bounds taiki_b = taiki_useCustomBounds ? taiki_bounds : taiki_SceneBounds();
        if (taiki_b.size == Vector3.zero) { EditorUtility.DisplayDialog("taiki", "コライダーが見つかりません", "OK"); return; }

        string taiki_defaultName = $"layout_{taiki_scene.name}.json";
        string taiki_path = EditorUtility.SaveFilePanel("間取りJSONの保存先", Application.dataPath + "/..", taiki_defaultName, "json");
        if (string.IsNullOrEmpty(taiki_path)) return;

        // シーン内のコライダーをすべて有効にした状態で調べる（プレイ中でなくても Physics クエリは使える）
        Physics.SyncTransforms();

        int taiki_nx = Mathf.CeilToInt(taiki_b.size.x / taiki_cell);
        int taiki_nz = Mathf.CeilToInt(taiki_b.size.z / taiki_cell);
        float taiki_x0 = taiki_b.min.x, taiki_z0 = taiki_b.min.z;
        float taiki_top = taiki_b.max.y + 1f, taiki_bottom = taiki_b.min.y - 1f;
        var IC = CultureInfo.InvariantCulture;

        var taiki_sb = new StringBuilder(1 << 20);
        int taiki_count = 0, taiki_walk = 0;
        try
        {
            for (int i = 0; i < taiki_nx; i++)
            {
                if (i % 10 == 0 && EditorUtility.DisplayCancelableProgressBar("taiki 間取り書き出し", $"{i}/{taiki_nx} 列", (float)i / taiki_nx))
                    return;
                for (int j = 0; j < taiki_nz; j++)
                {
                    float x = taiki_x0 + (i + 0.5f) * taiki_cell, z = taiki_z0 + (j + 0.5f) * taiki_cell;
                    Vector3 taiki_origin = new Vector3(x, taiki_top, z);
                    for (int k = 0; k < 64; k++)  // 上から順に面を拾っていく（1列あたり最大64面）
                    {
                        float taiki_dist = taiki_origin.y - taiki_bottom;
                        if (taiki_dist <= 0f) break;
                        if (!taiki_RaycastIgnoringPlayer(taiki_origin, taiki_dist, out RaycastHit taiki_hit)) break;
                        if (taiki_hit.normal.y >= taiki_maxSlopeNormalY)
                        {
                            bool taiki_ok = taiki_HasClearance(taiki_hit.point);
                            if (taiki_count > 0) taiki_sb.Append(',');
                            taiki_sb.Append('[').Append(i).Append(',').Append(j).Append(',')
                                    .Append(taiki_hit.point.y.ToString("F3", IC)).Append(',').Append(taiki_ok ? 1 : 0).Append(']');
                            taiki_count++;
                            if (taiki_ok) taiki_walk++;
                        }
                        taiki_origin = new Vector3(x, taiki_hit.point.y - 0.02f, z);
                    }
                }
            }
        }
        finally { EditorUtility.ClearProgressBar(); }

        // ---- 目印（炎・障害物・スタート・ゴール）----
        var taiki_markers = new List<string>();
        var taiki_done = new HashSet<GameObject>();
        foreach (var m in FindObjectsByType<taiki_MapMarker>(FindObjectsSortMode.None))
        {
            taiki_AddMarker(taiki_markers, m.taiki_kind.ToString().ToLower(), string.IsNullOrEmpty(m.taiki_label) ? m.name : m.taiki_label, m, m.taiki_sizeXZ);
            taiki_done.Add(m.gameObject);
        }
        foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
        {
            if (mb == null || taiki_done.Contains(mb.gameObject)) continue;
            string t = mb.GetType().Name;
            if (t == "FireVisual") { taiki_AddMarker(taiki_markers, "fire", mb.name, mb, Vector2.zero); taiki_done.Add(mb.gameObject); }
            else if (t == "StartZone") { taiki_AddMarker(taiki_markers, "start", mb.name, mb, Vector2.zero); taiki_done.Add(mb.gameObject); }
            else if (t == "GoalZone") { taiki_AddMarker(taiki_markers, "goal", mb.name, mb, Vector2.zero); taiki_done.Add(mb.gameObject); }
        }

        // ---- 階の設定 ----
        var taiki_floors = new List<string>();
        foreach (var part in taiki_floorText.Split(','))
        {
            var f = part.Trim().Split(':');
            if (f.Length != 3) continue;
            if (float.TryParse(f[1], NumberStyles.Float, IC, out float lo) && float.TryParse(f[2], NumberStyles.Float, IC, out float hi))
                taiki_floors.Add("{" + string.Format(IC, "\"name\":\"{0}\",\"y_min\":{1},\"y_max\":{2}", taiki_Json(f[0].Trim()), lo, hi) + "}");
        }

        var taiki_json = new StringBuilder();
        taiki_json.Append("{\n");
        taiki_json.AppendFormat(IC, "\"version\":1,\n\"scene\":\"{0}\",\n\"cell\":{1},\n\"origin\":[{2:F3},{3:F3}],\n\"nx\":{4},\n\"nz\":{5},\n",
                                taiki_Json(taiki_scene.name), taiki_cell, taiki_x0, taiki_z0, taiki_nx, taiki_nz);
        taiki_json.AppendFormat(IC, "\"agent_height\":{0},\n\"step_height\":{1},\n", taiki_agentHeight, taiki_stepHeight);
        taiki_json.Append("\"floors\":[").Append(string.Join(",", taiki_floors)).Append("],\n");
        taiki_json.Append("\"markers\":[\n").Append(string.Join(",\n", taiki_markers)).Append("\n],\n");
        taiki_json.Append("\"surfaces\":[").Append(taiki_sb).Append("]\n}\n");
        File.WriteAllText(taiki_path, taiki_json.ToString(), new UTF8Encoding(false));

        Debug.Log($"[taiki_LayoutExporter] 書き出し完了: {taiki_path}  格子 {taiki_nx}x{taiki_nz} / 面 {taiki_count}（歩ける {taiki_walk}）/ 目印 {taiki_markers.Count}");
        EditorUtility.RevealInFinder(taiki_path);
    }

    // 目印1つ分の JSON を追加する
    private static void taiki_AddMarker(List<string> taiki_markers, string kind, string label, Component c, Vector2 sizeXZ)
    {
        var IC = CultureInfo.InvariantCulture;
        Bounds mb = taiki_ObjectBounds(c.gameObject);
        Vector2 s = sizeXZ != Vector2.zero ? sizeXZ : new Vector2(mb.size.x, mb.size.z);
        Vector3 p = mb.size == Vector3.zero ? c.transform.position : mb.center;
        // ※ 書式の中に { } を入れると Unity(Mono) で "F3}" のように崩れるので、外側の { } は文字列連結で付ける
        string taiki_body = string.Format(IC,
            "\"kind\":\"{0}\",\"label\":\"{1}\",\"x\":{2:F3},\"y\":{3:F3},\"z\":{4:F3},\"sx\":{5:F3},\"sz\":{6:F3}",
            kind, taiki_Json(label), p.x, mb.size == Vector3.zero ? p.y : mb.min.y, p.z, s.x, s.y);
        taiki_markers.Add("{" + taiki_body + "}");
    }

    // Player とトリガーを無視して、下向きに一番近い面を探す
    private static bool taiki_RaycastIgnoringPlayer(Vector3 origin, float dist, out RaycastHit best)
    {
        best = default(RaycastHit);
        var taiki_hits = Physics.RaycastAll(origin, Vector3.down, dist, ~0, QueryTriggerInteraction.Ignore);
        float taiki_min = float.MaxValue;
        bool taiki_found = false;
        foreach (var h in taiki_hits)
        {
            if (taiki_IsPlayer(h.collider)) continue;
            if (h.distance < taiki_min) { taiki_min = h.distance; best = h; taiki_found = true; }
        }
        return taiki_found;
    }

    // 面の上に人が立てる空間があるか
    private bool taiki_HasClearance(Vector3 floorPoint)
    {
        float taiki_r = Mathf.Max(0.05f, taiki_agentRadius);
        Vector3 taiki_p1 = floorPoint + Vector3.up * (taiki_stepHeight * 0.8f + taiki_r);   // 越えられる段差より上から
        Vector3 taiki_p2 = floorPoint + Vector3.up * (taiki_agentHeight - taiki_r);
        var taiki_cols = Physics.OverlapCapsule(taiki_p1, taiki_p2, taiki_r, ~0, QueryTriggerInteraction.Ignore);
        foreach (var c in taiki_cols)
            if (!taiki_IsPlayer(c)) return false;
        return true;
    }

    private static bool taiki_IsPlayer(Collider c)
    {
        Transform t = c.transform;
        while (t != null) { if (t.CompareTag("Player")) return true; t = t.parent; }
        return false;
    }

    // Hierarchy で選んだオブジェクト（子も含む）が収まる範囲を、調べる範囲にする
    private void taiki_SetBoundsFromSelection()
    {
        var taiki_sel = Selection.gameObjects;
        if (taiki_sel.Length == 0) { EditorUtility.DisplayDialog("taiki", "Hierarchy でオブジェクトを選んでから押してください", "OK"); return; }
        Bounds b = taiki_ObjectBounds(taiki_sel[0]);
        foreach (var go in taiki_sel) b.Encapsulate(taiki_ObjectBounds(go));
        b.Expand(2f);
        taiki_bounds = b;
        taiki_useCustomBounds = true;
        Repaint();
    }

    private static Bounds taiki_SceneBounds()
    {
        Bounds b = new Bounds();
        bool first = true;
        foreach (var c in FindObjectsByType<Collider>(FindObjectsSortMode.None))
        {
            if (!c.enabled || c.isTrigger || taiki_IsPlayer(c)) continue;
            if (first) { b = c.bounds; first = false; } else b.Encapsulate(c.bounds);
        }
        return b;
    }

    private static Bounds taiki_ObjectBounds(GameObject go)
    {
        var cols = go.GetComponentsInChildren<Collider>();
        if (cols.Length > 0) { Bounds b = cols[0].bounds; foreach (var c in cols) b.Encapsulate(c.bounds); return b; }
        var rens = go.GetComponentsInChildren<Renderer>().Where(r => !(r is ParticleSystemRenderer)).ToArray();
        if (rens.Length > 0) { Bounds b = rens[0].bounds; foreach (var r in rens) b.Encapsulate(r.bounds); return b; }
        return new Bounds(go.transform.position, Vector3.zero);
    }

    private static string taiki_Json(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
}
#endif
