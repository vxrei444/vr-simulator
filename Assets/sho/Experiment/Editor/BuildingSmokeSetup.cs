#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 今開いているシーン（実験場など）の講義棟に、「最上階の天井から順番に煙がたまる」設定を自動で入れる。
/// メニュー: Tools > 避難実験 > 今のシーンの建物に煙を設定
///
///   ・LectureBuilding_corridor-1/2/3（なければ room1/2/3）の位置から、各階の床と天井の高さを自動で求める
///   ・建物（buildings-right / building-left など）ごとの範囲に煙の箱を作る
///   ・プレイヤーの目の高さから「1m が何ユニットか」を推定して、霧の濃さを実寸に合わせる
///   ・Player に SmokeExposure（煙の症状）を付ける
/// 値はあとから SmokeSystem の Inspector で自由に直せる。
/// </summary>
public static class BuildingSmokeSetup
{
    const string Gen = "Assets/sho/Experiment/Generated";

    // 建物の解析結果
    class Detected
    {
        public List<SmokeLayerController.FloorLevel> floors = new List<SmokeLayerController.FloorLevel>();
        public Dictionary<Transform, Bounds> buildingRoots = new Dictionary<Transform, Bounds>();
        public float upm = 1f;
        public Camera cam;
        public GameObject player;
    }

    static Detected Detect()
    {
        var d = new Detected();
        var all = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

        // ---- 1. 階ごとの床・天井を求める ----
        // 部品ごとの「下端」「上端」を集め、外れ値（階段など下の階まで伸びる部品）に強い
        // 分位点（下端は20%点、上端は80%点）で床・天井を決める
        var corridorRx = new Regex(@"LectureBuilding_corridor-?(\d)");
        var roomRx = new Regex(@"LectureBuilding_room(\d)");
        var minsC = new Dictionary<int, List<float>>(); var maxsC = new Dictionary<int, List<float>>();
        var minsR = new Dictionary<int, List<float>>(); var maxsR = new Dictionary<int, List<float>>();
        foreach (var r in all)
        {
            if (r is ParticleSystemRenderer) continue;
            Transform inst = FindAncestor(r.transform, t => t.name.StartsWith("LectureBuilding_"));
            if (inst == null) continue;
            var mc = corridorRx.Match(inst.name);
            var mr = roomRx.Match(inst.name);
            if (mc.Success) { int n = int.Parse(mc.Groups[1].Value); Add(minsC, n, r.bounds.min.y); Add(maxsC, n, r.bounds.max.y); }
            else if (mr.Success) { int n = int.Parse(mr.Groups[1].Value); Add(minsR, n, r.bounds.min.y); Add(maxsR, n, r.bounds.max.y); }
            Transform root = inst.parent != null ? inst.parent : inst;
            Encap(d.buildingRoots, root, r.bounds);
        }
        var mins = minsC.Count > 0 ? minsC : minsR;
        var maxs = minsC.Count > 0 ? maxsC : maxsR;
        var nums = mins.Keys.OrderBy(k => k).ToList();
        var fl = new List<float>(); var cl = new List<float>();
        foreach (var n in nums) { fl.Add(Quantile(mins[n], 0.2f)); cl.Add(Quantile(maxs[n], 0.8f)); }
        // 下から順に整える：床は単調に増える、天井は上の階の床を超えない
        for (int i = 1; i < fl.Count; i++) if (fl[i] <= fl[i - 1]) fl[i] = cl[i - 1];
        for (int i = 0; i < nums.Count; i++)
        {
            float c = cl[i];
            if (i + 1 < nums.Count) c = Mathf.Min(c, fl[i + 1]);
            if (c <= fl[i] + 0.5f) c = i + 1 < nums.Count ? fl[i + 1] : fl[i] + (fl.Count > 1 ? fl[1] - fl[0] : 3f);
            d.floors.Add(new SmokeLayerController.FloorLevel { name = nums[i] + "F", floorY = fl[i], ceilingY = c, fillSeconds = 60f });
        }

        // ---- 2. 1m が何ユニットか（プレイヤーの目の高さ ÷ 1.6m） ----
        var pm = Object.FindAnyObjectByType<Sho_PlayerMovements>();
        d.player = pm != null ? pm.gameObject : null;
        if (d.player == null)
        {
            var cc0 = Object.FindAnyObjectByType<CharacterController>();
            if (cc0 != null) d.player = cc0.gameObject;
        }
        d.cam = Camera.main != null ? Camera.main : Object.FindAnyObjectByType<Camera>();
        if (d.player != null && d.cam != null)
        {
            var cc = d.player.GetComponent<CharacterController>();
            if (cc != null)
            {
                float feet = d.player.transform.TransformPoint(cc.center).y - cc.height * 0.5f * Mathf.Abs(d.player.transform.lossyScale.y);
                float eye = d.cam.transform.position.y - feet;
                if (eye > 0.1f) d.upm = Mathf.Clamp(eye / 1.6f, 0.2f, 20f);
            }
        }
        return d;
    }

    static void Add(Dictionary<int, List<float>> d, int k, float v)
    {
        if (!d.TryGetValue(k, out var l)) d[k] = l = new List<float>();
        l.Add(v);
    }

    static float Quantile(List<float> v, float q)
    {
        var a = v.OrderBy(x => x).ToList();
        if (a.Count == 0) return 0f;
        return a[Mathf.Clamp(Mathf.RoundToInt(q * (a.Count - 1)), 0, a.Count - 1)];
    }

    static string FloorText(List<SmokeLayerController.FloorLevel> floors) =>
        string.Join("\n", floors.OrderByDescending(f => f.floorY).Select(f => $"  {f.name}: 床 {f.floorY:F1} / 天井 {f.ceilingY:F1}"));

    [MenuItem("Tools/避難実験/煙：階の高さと縮尺を再検出（設定はそのまま）")]
    public static void Redetect()
    {
        var s = Object.FindAnyObjectByType<SmokeLayerController>();
        if (s == null) { EditorUtility.DisplayDialog("煙の設定", "SmokeSystem がありません。先に「今のシーンの建物に煙を設定」を実行してください。", "OK"); return; }
        var d = Detect();
        if (d.floors.Count == 0) { EditorUtility.DisplayDialog("煙の設定", "建物（LectureBuilding_corridor-1 など）が見つかりませんでした。", "OK"); return; }
        Undo.RecordObject(s, "Redetect floors");
        // 各階の秒数は今の値を引き継ぐ
        foreach (var f in d.floors)
        {
            var old = s.floors != null ? s.floors.FirstOrDefault(o => o.name == f.name) : null;
            if (old != null) f.fillSeconds = old.fillSeconds;
        }
        s.floors = d.floors;
        s.worldUnitsPerMeter = d.upm;
        s.ResetSim();
        EditorUtility.SetDirty(s);
        EditorSceneManager.MarkSceneDirty(s.gameObject.scene);
        EditorUtility.DisplayDialog("煙の設定", "階の高さと縮尺を更新しました。\n\n" + FloorText(d.floors) +
            $"\n\n1m = {d.upm:F2} ユニット\nシーンを保存（Ctrl+S）してください。", "OK");
    }

    [MenuItem("Tools/避難実験/今のシーンの建物に煙を設定")]
    public static void Run()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var det = Detect();
        if (det.floors.Count == 0)
        {
            EditorUtility.DisplayDialog("煙の設定", "LectureBuilding_corridor-1 / room1 などの建物が見つかりませんでした。\n建物をシーンに置いてから実行してください。", "OK");
            return;
        }
        var floors = det.floors;
        var buildingRoots = det.buildingRoots;
        float upm = det.upm;
        var player = det.player;
        var cam = det.cam;

        // ---- 3. SmokeSystem を作る（既にあれば作り直す） ----
        var old = GameObject.Find("SmokeSystem");
        if (old != null)
        {
            if (!EditorUtility.DisplayDialog("煙の設定", "SmokeSystem が既にあります。作り直しますか？", "作り直す", "やめる")) return;
            Undo.DestroyObjectImmediate(old);
        }
        var smokeMat = AssetDatabase.LoadAssetAtPath<Material>(Gen + "/SmokeVolume.mat");
        if (smokeMat == null)
            Debug.LogWarning("[BuildingSmokeSetup] SmokeVolume.mat が見つかりません。先に Tools > 避難実験 > testScene を作成 を一度実行すると作られます。");

        var go = new GameObject("SmokeSystem");
        Undo.RegisterCreatedObjectUndo(go, "Create SmokeSystem");
        var s = go.AddComponent<SmokeLayerController>();
        s.mode = SmokeLayerController.Mode.FloorByFloor;
        s.worldUnitsPerMeter = upm;
        s.floors = floors;
        s.floorGapSeconds = 0f;
        s.nextFloorStartFraction = 0.5f;
        s.initialDensity = 0.35f;
        s.continueAfterSessionEnd = true;
        s.minLayerHeight = 0.8f;      // 各階で床上 0.8m まで下がる（床近くに煙の薄い層が残る）
        s.linearExtinction = 2.5f;    // 見通し 約3m（光る看板なら 8/2.5）
        s.linearCOppm = 400f;
        s.lowerLayerLeak = 0.05f;
        s.preburnSeconds = 0f;
        s.linearDelay = 0f;
        s.fogScale = 1f;
        s.interfaceBlend = 0.2f;
        s.viewer = cam != null ? cam.transform : null;
        s.smokeVolumeMaterial = smokeMat;
        s.regions = new List<SmokeLayerController.Region>();
        foreach (var kv in buildingRoots)
        {
            var b = kv.Value;
            var reg = new SmokeLayerController.Region
            {
                name = kv.Key.name,
                centerXZ = new Vector2(b.center.x, b.center.z),
                sizeXZ = new Vector2(b.size.x, b.size.z),
            };
            var vol = GameObject.CreatePrimitive(PrimitiveType.Cube);
            vol.name = "SmokeVolume_" + kv.Key.name;
            Object.DestroyImmediate(vol.GetComponent<Collider>());
            vol.transform.SetParent(go.transform, false);
            var rend = vol.GetComponent<Renderer>();
            if (smokeMat != null) rend.sharedMaterial = smokeMat;
            rend.shadowCastingMode = ShadowCastingMode.Off;
            rend.receiveShadows = false;
            vol.SetActive(false);
            reg.volume = vol.transform;
            s.regions.Add(reg);
        }
        s.ResetSim();

        // 霧を有効にしておく（ビルドで霧の機能が削られないように）
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogDensity = 0.01f / upm;

        // ---- 4. Player に煙の症状を付ける ----
        string exposureMsg = "";
        if (player != null && cam != null)
        {
            var ex = player.GetComponent<SmokeExposure>();
            if (ex == null) ex = Undo.AddComponent<SmokeExposure>(player);
            ex.smoke = s;
            ex.head = cam.transform;
            ex.movements = player.GetComponent<Sho_PlayerMovements>();
            ex.overlay = EnsurePlate(cam.transform, "SymptomVignette", Gen + "/SymptomVignette.mat", 0.12f);
            ex.blackout = EnsurePlate(cam.transform, "SymptomBlackout", Gen + "/SymptomBlackout.mat", 0.1f);
            exposureMsg = "\nPlayer に SmokeExposure（煙の症状）を付けました。";
        }

        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeGameObject = go;

        string floorText = FloorText(floors);
        EditorUtility.DisplayDialog("煙の設定",
            "SmokeSystem を作りました（上の階から順に充満）。\n\n" + floorText +
            $"\n\n1m = {upm:F2} ユニット（プレイヤーの目の高さから推定）" + exposureMsg +
            "\n\n各階が埋まる秒数は SmokeSystem > Floors > Fill Seconds で変えられます。\nシーンを保存（Ctrl+S）してください。", "OK");
    }

    static Transform FindAncestor(Transform t, System.Func<Transform, bool> pred)
    {
        while (t != null) { if (pred(t)) return t; t = t.parent; }
        return null;
    }

    static void Encap<TK>(Dictionary<TK, Bounds> d, TK key, Bounds b)
    {
        if (d.TryGetValue(key, out var cur)) { cur.Encapsulate(b); d[key] = cur; }
        else d[key] = b;
    }

    static Renderer EnsurePlate(Transform cam, string name, string matPath, float dist)
    {
        var existing = cam.Find(name);
        GameObject q;
        if (existing != null) q = existing.gameObject;
        else
        {
            q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = name;
            Object.DestroyImmediate(q.GetComponent<Collider>());
            Undo.RegisterCreatedObjectUndo(q, "Create plate");
            q.transform.SetParent(cam, false);
            q.transform.localPosition = new Vector3(0f, 0f, dist);
        }
        var r = q.GetComponent<Renderer>();
        var m = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (m != null) r.sharedMaterial = m;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.enabled = false;
        return r;
    }
}
#endif
