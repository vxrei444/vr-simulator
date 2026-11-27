#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 【taiki】今開いているシーンを「真上から」撮影して、各階の見取り図（PNG）を作るエディタ機能。
/// メニュー: Tools > taiki > 階の見取り図を撮影（真上から）
///
/// しくみ：その階の天井より少し下に、真下を向いた「正投影カメラ」を置いて撮影する。
///   カメラより上（上の階・天井）は写らず、床から少し下までしか写さないので、1つの階だけが写る。
///   撮影範囲（x, z の最小・最大）をそのまま Python の imshow(extent=...) に使えば、
///   ログの座標と見取り図がぴったり重なる。
/// </summary>
public class taiki_TopViewCapture : EditorWindow
{
    private float taiki_xMin = -230f, taiki_xMax = 70f, taiki_zMin = -60f, taiki_zMax = 60f;
    private string taiki_floorList = "1F:0, 2F:13, 3F:26";   // 階の名前:床の高さ
    private float taiki_cameraAboveFloor = 10f;            // 床からカメラまでの高さ（天井より低く）
    private float taiki_belowFloor = 1f;                   // 床より下に何mまで写すか
    private int taiki_pixelsPerUnit = 8;
    private bool taiki_hideSmoke = true;

    [MenuItem("Tools/taiki/階の見取り図を撮影（真上から）")]
    private static void taiki_Open()
    {
        var taiki_w = GetWindow<taiki_TopViewCapture>("taiki 見取り図撮影");
        taiki_w.minSize = new Vector2(400, 330);
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("撮影範囲（ログの座標と同じ x, z）", EditorStyles.boldLabel);
        taiki_xMin = EditorGUILayout.FloatField("x 最小（西）", taiki_xMin);
        taiki_xMax = EditorGUILayout.FloatField("x 最大（東）", taiki_xMax);
        taiki_zMin = EditorGUILayout.FloatField("z 最小（南）", taiki_zMin);
        taiki_zMax = EditorGUILayout.FloatField("z 最大（北）", taiki_zMax);
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("階", EditorStyles.boldLabel);
        taiki_floorList = EditorGUILayout.TextField("名前:床の高さ", taiki_floorList);
        taiki_cameraAboveFloor = EditorGUILayout.FloatField("カメラの高さ（床から）", taiki_cameraAboveFloor);
        taiki_belowFloor = EditorGUILayout.FloatField("床より下も写す量", taiki_belowFloor);
        EditorGUILayout.HelpBox("カメラの高さは「天井より低く」。天井が写り込んで真っ黒/真っ白なら小さく、壁が写らなければ大きくする。", MessageType.Info);
        EditorGUILayout.Space();
        taiki_pixelsPerUnit = EditorGUILayout.IntSlider("解像度（1単位あたりの画素）", taiki_pixelsPerUnit, 2, 20);
        taiki_hideSmoke = EditorGUILayout.Toggle("煙（名前に Smoke）を隠して撮る", taiki_hideSmoke);
        int taiki_w = Mathf.RoundToInt((taiki_xMax - taiki_xMin) * taiki_pixelsPerUnit);
        int taiki_h = Mathf.RoundToInt((taiki_zMax - taiki_zMin) * taiki_pixelsPerUnit);
        EditorGUILayout.LabelField($"画像の大きさ：{taiki_w} × {taiki_h} 画素");
        EditorGUILayout.Space();
        if (GUILayout.Button("撮影する", GUILayout.Height(32))) taiki_CaptureAll();
    }

    private void taiki_CaptureAll()
    {
        var IC = CultureInfo.InvariantCulture;
        string taiki_dir = EditorUtility.SaveFolderPanel("見取り図の保存先フォルダ", Application.dataPath + "/..", "");
        if (string.IsNullOrEmpty(taiki_dir)) return;

        // 煙を一時的に隠す
        var taiki_hidden = new List<GameObject>();
        if (taiki_hideSmoke)
        {
            foreach (var go in FindObjectsByType<GameObject>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (go.name.Contains("Smoke") && go.activeSelf) { go.SetActive(false); taiki_hidden.Add(go); }
            }
        }

        var taiki_info = new System.Text.StringBuilder();
        taiki_info.AppendLine("floor,file,x_min,x_max,z_min,z_max,floor_y");
        try
        {
            foreach (var part in taiki_floorList.Split(','))
            {
                var f = part.Trim().Split(':');
                if (f.Length != 2 || !float.TryParse(f[1], NumberStyles.Float, IC, out float taiki_floorY)) continue;
                string taiki_name = f[0].Trim();
                string taiki_file = $"map_{taiki_name}.png";
                taiki_CaptureOne(taiki_floorY, Path.Combine(taiki_dir, taiki_file));
                taiki_info.AppendLine(string.Format(IC, "{0},{1},{2},{3},{4},{5},{6}",
                    taiki_name, taiki_file, taiki_xMin, taiki_xMax, taiki_zMin, taiki_zMax, taiki_floorY));
            }
        }
        finally
        {
            foreach (var go in taiki_hidden) if (go != null) go.SetActive(true);
        }
        File.WriteAllText(Path.Combine(taiki_dir, "map_info.csv"), taiki_info.ToString());
        Debug.Log($"[taiki_TopViewCapture] 撮影完了: {taiki_dir}\n{taiki_info}");
        EditorUtility.RevealInFinder(Path.Combine(taiki_dir, "map_info.csv"));
    }

    private void taiki_CaptureOne(float taiki_floorY, string taiki_path)
    {
        int taiki_w = Mathf.RoundToInt((taiki_xMax - taiki_xMin) * taiki_pixelsPerUnit);
        int taiki_h = Mathf.RoundToInt((taiki_zMax - taiki_zMin) * taiki_pixelsPerUnit);

        var taiki_go = new GameObject("taiki_TopViewCamera (temp)");
        try
        {
            var taiki_cam = taiki_go.AddComponent<Camera>();
            taiki_cam.orthographic = true;
            taiki_cam.orthographicSize = (taiki_zMax - taiki_zMin) / 2f;      // 縦の半分
            taiki_cam.aspect = (float)taiki_w / taiki_h;
            taiki_cam.nearClipPlane = 0.01f;
            taiki_cam.farClipPlane = taiki_cameraAboveFloor + taiki_belowFloor;
            taiki_cam.clearFlags = CameraClearFlags.SolidColor;
            taiki_cam.backgroundColor = Color.white;
            taiki_go.transform.position = new Vector3((taiki_xMin + taiki_xMax) / 2f, taiki_floorY + taiki_cameraAboveFloor, (taiki_zMin + taiki_zMax) / 2f);
            taiki_go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);       // 真下を向く（画像の上＝北(+z)）

            var taiki_rt = new RenderTexture(taiki_w, taiki_h, 24, RenderTextureFormat.ARGB32);
            taiki_cam.targetTexture = taiki_rt;

            var taiki_req = new UniversalRenderPipeline.SingleCameraRequest { destination = taiki_rt };
            if (RenderPipeline.SupportsRenderRequest(taiki_cam, taiki_req))
                RenderPipeline.SubmitRenderRequest(taiki_cam, taiki_req);
            else
                taiki_cam.Render();

            var taiki_prev = RenderTexture.active;
            RenderTexture.active = taiki_rt;
            var taiki_tex = new Texture2D(taiki_w, taiki_h, TextureFormat.RGB24, false);
            taiki_tex.ReadPixels(new Rect(0, 0, taiki_w, taiki_h), 0, 0);
            taiki_tex.Apply();
            RenderTexture.active = taiki_prev;

            File.WriteAllBytes(taiki_path, taiki_tex.EncodeToPNG());
            taiki_cam.targetTexture = null;
            DestroyImmediate(taiki_tex);
            taiki_rt.Release();
            DestroyImmediate(taiki_rt);
        }
        finally
        {
            DestroyImmediate(taiki_go);
        }
    }
}
#endif
