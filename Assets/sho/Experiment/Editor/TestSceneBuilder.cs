#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// 対照実験用のテストシーン（Assets/sho/testScene.unity）を自動で組み立てる。
/// メニュー: Tools > 避難実験 > testScene を作成
///
/// できるもの
///   ・T字路＋行き止まりのある廊下（天井 2.7m、幅 2.5m）、出口、火元
///   ・Player（PC操作用：WASD / Shift / C でしゃがむ）＋ 目の高さ追従 ＋ 煙の症状
///   ・EvacuationSessionManager / StartZone / GoalZone / ログ記録 / 分岐点ゾーン
///   ・誘導条件 A〜E のサイン一式（床矢印・光る看板・点滅灯・動く矢印）
///   ・煙層（二層ゾーンモデル）と霧
/// 生成した素材は Assets/sho/Experiment/Generated/ に入る。
/// </summary>
public static class TestSceneBuilder
{
    const string Root = "Assets/sho/Experiment";
    const string Gen = Root + "/Generated";
    const string ScenePath = "Assets/sho/testScene.unity";
    const float H = 2.7f;   // 天井高
    const float WT = 0.2f;  // 壁の厚さ

    // 素材
    static Material mFloor, mWall, mCeil, mOutside, mLamp, mArrow, mChevron, mSignUp, mSignRight, mSignExit,
                    mBeacon, mSmoke, mSmokeParticle, mFlame, mVignette, mBlack, mFireBase;

    [MenuItem("Tools/避難実験/testScene を作成")]
    public static void Build()
    {
        if (File.Exists(ScenePath) &&
            !EditorUtility.DisplayDialog("testScene", "testScene.unity は既にあります。作り直しますか？\n（今の testScene の変更は消えます）", "作り直す", "やめる"))
            return;
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        try
        {
            EditorUtility.DisplayProgressBar("testScene", "素材を作成中…", 0.1f);
            MakeAssets();

            EditorUtility.DisplayProgressBar("testScene", "シーンを作成中…", 0.4f);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SetupLighting();

            var env = new GameObject("Environment").transform;
            BuildCorridors(env);

            var player = BuildPlayer(out Camera cam, out SmokeExposure exposure);
            var session = BuildSession(player, cam, out ExperimentConditionManager cond);
            var smoke = BuildSmoke(cam.transform);
            exposure.smoke = smoke;
            cond.smokeController = smoke;
            BuildFire(smoke);
            BuildDecisionPoints();
            cond.groups = BuildGuidance().ToArray();
            cond.ApplyAll();

            EditorUtility.DisplayProgressBar("testScene", "保存中…", 0.9f);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();
            Selection.activeGameObject = session;
            Debug.Log("[TestSceneBuilder] testScene を作成しました: " + ScenePath);
            EditorUtility.DisplayDialog("testScene", "testScene を作成しました。\n\n" +
                "▶ 再生して WASD で移動、マウスで視点、C でしゃがむ。\n" +
                "スタート前にキー 1〜5 で誘導条件、Q/E で煙なし/標準 を切り替えられます。\n" +
                "条件は Hierarchy の EvacSession > ExperimentConditionManager でも変えられます。", "OK");
        }
        finally { EditorUtility.ClearProgressBar(); }
    }

    // ================= 素材 =================
    static void MakeAssets()
    {
        EnsureFolder("Assets/sho", "Experiment");
        EnsureFolder(Root, "Generated");

        var texArrow = SaveTex("Arrow", 256, 256, ArrowPixel, true);
        var texChevron = SaveTex("Chevron", 256, 256, ChevronPixel, true);
        var texSignUp = SaveTex("SignUp", 512, 256, (u, v) => SignPixel(u, v, 0), false);
        var texSignRight = SaveTex("SignRight", 512, 256, (u, v) => SignPixel(u, v, 1), false);
        var texSignExit = SaveTex("SignExit", 512, 256, (u, v) => SignPixel(u, v, 2), false);
        var texVignette = SaveTex("Vignette", 256, 256, VignettePixel, true);
        var texPuff = SaveTex("SmokePuff", 128, 128, PuffPixel, true);

        mFloor = Lit("Floor", new Color(0.45f, 0.45f, 0.43f));
        mWall = Lit("Wall", new Color(0.82f, 0.82f, 0.8f));
        mCeil = Lit("Ceiling", new Color(0.9f, 0.9f, 0.9f));
        mOutside = Lit("Outside", new Color(0.35f, 0.5f, 0.35f));
        mFireBase = Lit("FireBase", new Color(0.12f, 0.08f, 0.06f));
        mLamp = Unlit("CeilingLamp", new Color(0.95f, 0.95f, 0.9f), null, false);
        mArrow = Unlit("FloorArrow", Color.white, texArrow, true);
        mChevron = Unlit("FlowChevron", Color.white, texChevron, true);
        mSignUp = Unlit("SignStraight", Color.white, texSignUp, false);
        mSignRight = Unlit("SignRight", Color.white, texSignRight, false);
        mSignExit = Unlit("SignExit", Color.white, texSignExit, false);
        mBeacon = Unlit("BeaconLamp", Color.white, null, false);
        mSmoke = Transparent(Unlit("SmokeVolume", new Color(0.05f, 0.05f, 0.05f, 0.6f), null, false), 3000);
        mVignette = Transparent(Unlit("SymptomVignette", new Color(0.08f, 0.02f, 0.02f, 0f), texVignette, false), 4000);
        mBlack = Transparent(Unlit("SymptomBlackout", new Color(0f, 0f, 0f, 0f), null, false), 4001);
        mSmokeParticle = Particle("SmokeParticle", new Color(0.12f, 0.12f, 0.12f, 0.5f), texPuff, false);
        mFlame = Particle("FlameParticle", new Color(1f, 0.55f, 0.15f, 1f), texPuff, true);
        AssetDatabase.SaveAssets();
    }

    static void EnsureFolder(string parent, string name)
    {
        if (!AssetDatabase.IsValidFolder(parent + "/" + name)) AssetDatabase.CreateFolder(parent, name);
    }

    delegate Color PixelFn(float u, float v);

    static Texture2D SaveTex(string name, int w, int h, PixelFn fn, bool alpha)
    {
        string path = $"{Gen}/{name}.png";
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var px = new Color[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // 2x2 のスーパーサンプリングで縁をなめらかに
                Color c = Color.clear;
                for (int sy = 0; sy < 2; sy++)
                    for (int sx = 0; sx < 2; sx++)
                        c += fn((x + 0.25f + 0.5f * sx) / w, (y + 0.25f + 0.5f * sy) / h);
                px[y * w + x] = c * 0.25f;
            }
        tex.SetPixels(px);
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        ti.alphaIsTransparency = alpha;
        ti.wrapMode = TextureWrapMode.Clamp;
        ti.mipmapEnabled = true;
        ti.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    static bool InTri(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
    {
        float d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
        float d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
        float d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    // 上向きの矢印（白）
    static bool ArrowShape(float u, float v) =>
        (u > 0.38f && u < 0.62f && v > 0.08f && v < 0.58f) || InTri(u, v, 0.14f, 0.55f, 0.86f, 0.55f, 0.5f, 0.94f);

    static Color ArrowPixel(float u, float v) => ArrowShape(u, v) ? Color.white : Color.clear;

    static float SegDist(float px, float py, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay;
        float t = Mathf.Clamp01(((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy));
        float qx = ax + t * dx - px, qy = ay + t * dy - py;
        return Mathf.Sqrt(qx * qx + qy * qy);
    }

    // 上向きのシェブロン「^」
    static Color ChevronPixel(float u, float v)
    {
        float d = Mathf.Min(SegDist(u, v, 0.12f, 0.3f, 0.5f, 0.75f), SegDist(u, v, 0.5f, 0.75f, 0.88f, 0.3f));
        return d < 0.09f ? Color.white : Color.clear;
    }

    // 誘導看板（緑地に白）: kind 0=まっすぐ, 1=右, 2=出口（ドア＋上矢印）
    static Color SignPixel(float u, float v, int kind)
    {
        var green = new Color(0.0f, 0.55f, 0.25f, 1f);
        if (u < 0.02f || u > 0.98f || v < 0.04f || v > 0.96f) return Color.white; // 白い縁
        // 左側：出口を表すドア（白い枠）
        float du = (u - 0.08f) / 0.3f, dv = (v - 0.14f) / 0.72f;
        bool door = du >= 0 && du <= 1 && dv >= 0 && dv <= 1 && (du < 0.12f || du > 0.88f || dv > 0.9f || dv < 0.06f);
        if (door) return Color.white;
        if (kind == 2 && du >= 0 && du <= 1 && dv >= 0 && dv <= 1) return new Color(0.0f, 0.4f, 0.18f, 1f);
        // 右側：矢印
        float au = (u - 0.48f) / 0.42f, av = (v - 0.1f) / 0.8f;
        if (au >= 0 && au <= 1 && av >= 0 && av <= 1)
        {
            bool arrow = kind == 1 ? ArrowShape(av, 1f - au) : ArrowShape(au, av);
            if (arrow) return Color.white;
        }
        return green;
    }

    static Color VignettePixel(float u, float v)
    {
        float dx = (u - 0.5f) * 2f, dy = (v - 0.5f) * 2f;
        float r = Mathf.Sqrt(dx * dx + dy * dy);
        float a = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.3f, 1.05f, r));
        return new Color(1, 1, 1, a);
    }

    static Color PuffPixel(float u, float v)
    {
        float dx = (u - 0.5f) * 2f, dy = (v - 0.5f) * 2f;
        float r = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy));
        float n = Mathf.PerlinNoise(u * 6f, v * 6f) * 0.6f + Mathf.PerlinNoise(u * 13f + 7f, v * 13f) * 0.4f;
        float a = Mathf.Pow(1f - r, 2f) * Mathf.Lerp(0.5f, 1f, n);
        return new Color(1, 1, 1, a);
    }

    static Material SaveMat(string name, Material m)
    {
        string path = $"{Gen}/{name}.mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) { existing.shader = m.shader; existing.CopyPropertiesFromMaterial(m); existing.shaderKeywords = m.shaderKeywords; existing.renderQueue = m.renderQueue; Object.DestroyImmediate(m); EditorUtility.SetDirty(existing); return existing; }
        AssetDatabase.CreateAsset(m, path);
        return m;
    }

    static Shader FindShader(params string[] names)
    {
        foreach (var n in names) { var s = Shader.Find(n); if (s != null) return s; }
        Debug.LogError("[TestSceneBuilder] シェーダーが見つかりません: " + string.Join(", ", names));
        return Shader.Find("Hidden/InternalErrorShader");
    }

    static Material Lit(string name, Color c)
    {
        var m = new Material(FindShader("Universal Render Pipeline/Lit", "Standard"));
        m.SetColor("_BaseColor", c); m.SetColor("_Color", c);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.15f);
        m.enableInstancing = true;
        return SaveMat(name, m);
    }

    static Material Unlit(string name, Color c, Texture tex, bool alphaClip)
    {
        var m = new Material(FindShader("Universal Render Pipeline/Unlit", "Unlit/Texture"));
        m.SetColor("_BaseColor", c); m.SetColor("_Color", c);
        if (tex != null) { m.SetTexture("_BaseMap", tex); m.SetTexture("_MainTex", tex); }
        if (alphaClip)
        {
            m.SetFloat("_AlphaClip", 1f); m.SetFloat("_Cutoff", 0.5f);
            m.EnableKeyword("_ALPHATEST_ON");
            m.renderQueue = (int)RenderQueue.AlphaTest;
        }
        m.enableInstancing = true;
        return SaveMat(name, m);
    }

    static Material Transparent(Material m, int queue)
    {
        m.SetFloat("_Surface", 1f);
        m.SetFloat("_Blend", 0f);
        m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        if (m.HasProperty("_SrcBlendAlpha")) m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        if (m.HasProperty("_DstBlendAlpha")) m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0f);
        if (queue >= 4000 && m.HasProperty("_ZTest")) m.SetFloat("_ZTest", (float)CompareFunction.Always);
        m.SetOverrideTag("RenderType", "Transparent");
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.SetShaderPassEnabled("DepthOnly", false);
        m.SetShaderPassEnabled("ShadowCaster", false);
        m.renderQueue = queue;
        EditorUtility.SetDirty(m);
        return m;
    }

    static Material Particle(string name, Color c, Texture tex, bool additive)
    {
        var m = new Material(FindShader("Universal Render Pipeline/Particles/Unlit", "Particles/Standard Unlit"));
        m.SetColor("_BaseColor", c); m.SetColor("_Color", c);
        m.SetTexture("_BaseMap", tex); m.SetTexture("_MainTex", tex);
        m.SetFloat("_Surface", 1f);
        m.SetFloat("_Blend", additive ? 2f : 0f);
        m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", additive ? (float)BlendMode.One : (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0f);
        m.SetOverrideTag("RenderType", "Transparent");
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        if (additive) m.EnableKeyword("_BLENDMODE_ADD");
        m.renderQueue = (int)RenderQueue.Transparent;
        return SaveMat(name, m);
    }

    // ================= 環境 =================
    static void SetupLighting()
    {
        var sun = new GameObject("Directional Light").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 0.6f;
        sun.color = new Color(1f, 0.97f, 0.92f);
        sun.shadows = LightShadows.None;
        sun.transform.rotation = Quaternion.Euler(55f, -30f, 0f);

        RenderSettings.skybox = null;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.42f, 0.42f, 0.44f);
        // Fog を有効にして保存しておく（ビルド時に霧のシェーダー機能が削られないように）
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogDensity = 0.01f;
        RenderSettings.fogColor = new Color(0.25f, 0.25f, 0.27f);
    }

    static GameObject Box(string name, Transform parent, Vector3 center, Vector3 size, Material mat, bool collider = true)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
        return go;
    }

    // 廊下の床面（XZ の長方形）: 名前, xmin, xmax, zmin, zmax
    static readonly (string name, float x0, float x1, float z0, float z1)[] Regions =
    {
        ("StartCorridor", -1.25f, 1.25f, -3f, 24f),
        ("CrossCorridor", -12f, 20f, 24f, 26.5f),
        ("NorthDeadEnd", 7.75f, 10.25f, 26.5f, 36f),
    };

    static void BuildCorridors(Transform env)
    {
        var floors = new GameObject("Floors").transform; floors.SetParent(env);
        var ceils = new GameObject("Ceilings").transform; ceils.SetParent(env);
        var walls = new GameObject("Walls").transform; walls.SetParent(env);
        var lamps = new GameObject("CeilingLamps").transform; lamps.SetParent(env);

        foreach (var r in Regions)
        {
            Vector3 c = new Vector3((r.x0 + r.x1) / 2f, 0f, (r.z0 + r.z1) / 2f);
            Vector3 s = new Vector3(r.x1 - r.x0, 0.2f, r.z1 - r.z0);
            Box("Floor_" + r.name, floors, c + Vector3.down * 0.1f, s, mFloor);
            Box("Ceiling_" + r.name, ceils, c + Vector3.up * (H + 0.1f), s, mCeil);
        }

        // 壁（中心, 大きさ）
        void W(string n, float x0, float x1, float z0, float z1, float y0 = 0f, float y1 = H) =>
            Box(n, walls, new Vector3((x0 + x1) / 2f, (y0 + y1) / 2f, (z0 + z1) / 2f), new Vector3(x1 - x0, y1 - y0, z1 - z0), mWall);

        W("Start_West", -1.25f - WT, -1.25f, -3f - WT, 24f);
        W("Start_East", 1.25f, 1.25f + WT, -3f - WT, 24f);
        W("Start_Back", -1.25f - WT, 1.25f + WT, -3f - WT, -3f);
        W("Cross_South_W", -12f - WT, -1.25f, 24f - WT, 24f);
        W("Cross_South_E", 1.25f, 20f, 24f - WT, 24f);
        W("Cross_North_W", -12f - WT, 7.75f, 26.5f, 26.5f + WT);
        W("Cross_North_E", 10.25f, 20f, 26.5f, 26.5f + WT);
        W("Cross_WestEnd", -12f - WT, -12f, 24f - WT, 26.5f + WT);
        W("North_West", 7.75f - WT, 7.75f, 26.5f, 36f + WT);
        W("North_East", 10.25f, 10.25f + WT, 26.5f, 36f + WT);
        W("North_End", 7.75f - WT, 10.25f + WT, 36f, 36f + WT);
        W("Exit_Header", 20f, 20f + WT, 24f - WT, 26.5f + WT, 2.1f, H);
        W("Exit_PostS", 20f, 20f + WT, 24f - WT, 24.1f);
        W("Exit_PostN", 20f, 20f + WT, 26.4f, 26.5f + WT);

        // 屋外（出口の先）
        Box("Outside_Ground", env, new Vector3(24f, -0.1f, 25.25f), new Vector3(8f, 0.2f, 8f), mOutside);

        // 天井の照明（見た目だけ。ライトは使わず軽くする）
        foreach (var z in new[] { 0f, 6f, 12f, 18f })
            Box("Lamp", lamps, new Vector3(0f, H - 0.02f, z), new Vector3(0.3f, 0.03f, 1.2f), mLamp, false);
        foreach (var x in new[] { -9f, -3f, 3f, 9f, 15f })
            Box("Lamp", lamps, new Vector3(x, H - 0.02f, 25.25f), new Vector3(1.2f, 0.03f, 0.3f), mLamp, false);
        foreach (var z in new[] { 30f, 34f })
            Box("Lamp", lamps, new Vector3(9f, H - 0.02f, z), new Vector3(0.3f, 0.03f, 1.2f), mLamp, false);
    }

    // ================= プレイヤー =================
    static GameObject BuildPlayer(out Camera cam, out SmokeExposure exposure)
    {
        var player = new GameObject("Player");
        player.tag = "Player";
        player.transform.position = new Vector3(0f, 0.86f, 0f);
        var cc = player.AddComponent<CharacterController>();
        cc.height = 1.7f; cc.radius = 0.3f; cc.stepOffset = 0.3f; cc.slopeLimit = 45f; cc.center = Vector3.zero;
        var mv = player.AddComponent<Sho_PlayerMovements>();
        mv.walkSpeed = 1.4f;   // 実際の避難歩行速度に近い値（m/s）
        mv.fastSpeed = 2.5f;
        mv.crouchSpeed = 0.7f;

        var camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        camGo.transform.SetParent(player.transform, false);
        camGo.transform.localPosition = new Vector3(0f, 0.73f, 0f);
        cam = camGo.AddComponent<Camera>();
        cam.nearClipPlane = 0.05f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.2f, 0.2f, 0.22f);
        camGo.AddComponent<AudioListener>();
        camGo.AddComponent<EyeHeightFollower>().controller = cc;

        var vig = Plate("SymptomVignette", camGo.transform, mVignette, 0.12f);
        var blk = Plate("SymptomBlackout", camGo.transform, mBlack, 0.1f);

        exposure = player.AddComponent<SmokeExposure>();
        exposure.head = camGo.transform;
        exposure.movements = mv;
        exposure.overlay = vig;
        exposure.blackout = blk;
        return player;
    }

    static Renderer Plate(string name, Transform cam, Material mat, float dist)
    {
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        q.name = name;
        Object.DestroyImmediate(q.GetComponent<Collider>());
        q.transform.SetParent(cam, false);
        q.transform.localPosition = new Vector3(0f, 0f, dist);
        q.transform.localScale = new Vector3(0.3f, 0.2f, 1f);
        var r = q.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.enabled = false;
        return r;
    }

    // ================= セッション・スタート・ゴール =================
    static GameObject BuildSession(GameObject player, Camera cam, out ExperimentConditionManager cond)
    {
        var go = new GameObject("EvacSession");
        var m = go.AddComponent<EvacuationSessionManager>();
        m.timeLimit = 180f;
        var log = go.AddComponent<Sho_EvacuationTrajectoryLogger>();
        log.head = cam.transform;
        log.body = player.transform;
        cond = go.AddComponent<ExperimentConditionManager>();

        var start = Box("StartZone", null, new Vector3(0f, 1f, 0f), new Vector3(2.4f, 2f, 2.5f), mWall);
        Object.DestroyImmediate(start.GetComponent<Renderer>());
        GameObjectUtility.SetStaticEditorFlags(start, 0);
        start.GetComponent<Collider>().isTrigger = true;
        start.AddComponent<StartZone>().mode = StartZone.StartMode.OnExitZone;

        var goal = Box("GoalZone_Exit", null, new Vector3(22f, 1f, 25.25f), new Vector3(2f, 2f, 2.5f), mWall);
        Object.DestroyImmediate(goal.GetComponent<Renderer>());
        GameObjectUtility.SetStaticEditorFlags(goal, 0);
        goal.GetComponent<Collider>().isTrigger = true;
        goal.AddComponent<GoalZone>().goalName = "Exit_East";
        return go;
    }

    static void BuildDecisionPoints()
    {
        var parent = new GameObject("DecisionPoints").transform;
        void DP(string id, Vector3 c, Vector3 toExit, Vector3 toDead, Vector3 toBack)
        {
            var go = Box(id, parent, c, new Vector3(2.5f, 2.4f, 2.5f), mWall);
            Object.DestroyImmediate(go.GetComponent<Renderer>());
            GameObjectUtility.SetStaticEditorFlags(go, 0);
            go.GetComponent<Collider>().isTrigger = true;
            var dp = go.AddComponent<Sho_DecisionPointZone>();
            dp.pointId = id;
            dp.correctExitName = "toExit";
            var list = new List<Transform>();
            foreach (var (n, p) in new[] { ("toExit", toExit), ("toDeadEnd", toDead), ("toBack", toBack) })
            {
                var t = new GameObject(n).transform;
                t.SetParent(go.transform, true);
                t.position = p;
                list.Add(t);
            }
            dp.exits = list.ToArray();
        }
        DP("DP1_T", new Vector3(0f, 1.2f, 25.25f), new Vector3(3.5f, 1f, 25.25f), new Vector3(-3.5f, 1f, 25.25f), new Vector3(0f, 1f, 21.5f));
        DP("DP2_Side", new Vector3(9f, 1.2f, 25.25f), new Vector3(12.5f, 1f, 25.25f), new Vector3(9f, 1f, 29f), new Vector3(5.5f, 1f, 25.25f));
    }

    // ================= 煙・火 =================
    static SmokeLayerController BuildSmoke(Transform viewer)
    {
        var go = new GameObject("SmokeSystem");
        var s = go.AddComponent<SmokeLayerController>();
        s.floorY = 0f;
        s.ceilingHeight = H;
        s.viewer = viewer;
        s.smokeVolumeMaterial = mSmoke;
        s.regions = new List<SmokeLayerController.Region>();
        foreach (var r in Regions)
        {
            var reg = new SmokeLayerController.Region
            {
                name = r.name,
                centerXZ = new Vector2((r.x0 + r.x1) / 2f, (r.z0 + r.z1) / 2f),
                sizeXZ = new Vector2(r.x1 - r.x0, r.z1 - r.z0),
            };
            var vol = GameObject.CreatePrimitive(PrimitiveType.Cube);
            vol.name = "SmokeVolume_" + r.name;
            Object.DestroyImmediate(vol.GetComponent<Collider>());
            vol.transform.SetParent(go.transform, false);
            vol.transform.position = new Vector3(reg.centerXZ.x, H - 0.05f, reg.centerXZ.y);
            vol.transform.localScale = new Vector3(reg.sizeXZ.x, 0.1f, reg.sizeXZ.y);
            var rend = vol.GetComponent<Renderer>();
            rend.sharedMaterial = mSmoke;
            rend.shadowCastingMode = ShadowCastingMode.Off;
            rend.receiveShadows = false;
            reg.volume = vol.transform;
            s.regions.Add(reg);
        }

        // 煙層の下端でうごめく煙
        var pgo = new GameObject("InterfaceSmokeParticles");
        pgo.transform.SetParent(go.transform, false);
        pgo.transform.position = new Vector3(4f, H - 0.3f, 16.5f);
        pgo.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
        var ps = pgo.AddComponent<ParticleSystem>();
        var main = ps.main;
        main.loop = true;
        main.startLifetime = 7f;
        main.startSpeed = 0.05f;
        main.startSize = new ParticleSystem.MinMaxCurve(1.5f, 3f);
        main.startColor = new Color(0.1f, 0.1f, 0.1f, 0.45f);
        main.maxParticles = 350;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        var em = ps.emission; em.rateOverTime = 50f;
        var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(32f, 39.5f, 0.2f);
        var col = ps.colorOverLifetime; col.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                     new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.3f), new GradientAlphaKey(0f, 1f) });
        col.color = grad;
        var pr = pgo.GetComponent<ParticleSystemRenderer>();
        pr.sharedMaterial = mSmokeParticle;
        pr.shadowCastingMode = ShadowCastingMode.Off;
        s.interfaceParticles = ps;
        return s;
    }

    static void BuildFire(SmokeLayerController smoke)
    {
        var root = new GameObject("FireSource");
        root.transform.position = new Vector3(0f, 0f, -2.2f);
        Box("BurningObject", root.transform, new Vector3(0f, 0.3f, -2.2f), new Vector3(1.2f, 0.6f, 0.8f), mFireBase);

        var fgo = new GameObject("Flames");
        fgo.transform.SetParent(root.transform, false);
        fgo.transform.position = new Vector3(0f, 0.6f, -2.2f);
        fgo.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
        var ps = fgo.AddComponent<ParticleSystem>();
        var main = ps.main;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 0.9f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.8f, 1.6f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.3f, 0.7f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.75f, 0.2f), new Color(1f, 0.3f, 0.05f));
        main.maxParticles = 200;
        var em = ps.emission; em.rateOverTime = 60f;
        var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 12f; sh.radius = 0.4f;
        var sol = ps.sizeOverLifetime; sol.enabled = true;
        sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.1f));
        var pr = fgo.GetComponent<ParticleSystemRenderer>();
        pr.sharedMaterial = mFlame;
        pr.shadowCastingMode = ShadowCastingMode.Off;

        var lgo = new GameObject("FireLight");
        lgo.transform.SetParent(root.transform, false);
        lgo.transform.position = new Vector3(0f, 1.2f, -1.6f);
        var light = lgo.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(1f, 0.55f, 0.2f);
        light.range = 7f;
        light.shadows = LightShadows.None;

        var fv = root.AddComponent<FireVisual>();
        fv.smoke = smoke; fv.flames = ps; fv.fireLight = light;
    }

    // ================= 誘導サイン（条件 A〜E） =================
    static List<ExperimentConditionManager.GuidanceGroup> BuildGuidance()
    {
        var root = new GameObject("Guidance").transform;
        var groups = new List<ExperimentConditionManager.GuidanceGroup>();
        GameObject G(ExperimentConditionManager.Guidance g)
        {
            var go = new GameObject("Guidance_" + g);
            go.transform.SetParent(root, false);
            groups.Add(new ExperimentConditionManager.GuidanceGroup { guidance = g, objects = new[] { go } });
            return go;
        }

        // A: なし（比較の基準）
        G(ExperimentConditionManager.Guidance.A_None);

        // B: 床の矢印（蓄光風。常時点灯・控えめ）
        var b = G(ExperimentConditionManager.Guidance.B_FloorArrow).transform;
        int bi = 0;
        foreach (var z in new[] { 3f, 7f, 11f, 15f, 19f, 22.5f }) FloorArrow(b, ++bi, new Vector3(0f, 0f, z), Vector3.forward);
        FloorArrow(b, ++bi, new Vector3(0f, 0f, 25.25f), Vector3.right);
        foreach (var x in new[] { 4f, 7f, 9f, 12f, 15f, 18f }) FloorArrow(b, ++bi, new Vector3(x, 0f, 25.25f), Vector3.right);

        // C: 光る看板（常時点灯） / D: 点滅灯（同じ位置・同じ看板で、光り方だけが違う）
        BuildSigns(G(ExperimentConditionManager.Guidance.C_GlowSign).transform, "C", FlashingBeacon.Pattern.Steady, "glow_sign");
        BuildSigns(G(ExperimentConditionManager.Guidance.D_Flashing).transform, "D", FlashingBeacon.Pattern.Blink, "flashing_sign");

        // E: 動く矢印（床の光が出口へ流れる）
        var e = G(ExperimentConditionManager.Guidance.E_FlowingArrow).transform;
        var flowGo = new GameObject("FlowingArrows");
        flowGo.transform.SetParent(e, false);
        var wps = new List<Transform>();
        foreach (var p in new[] { new Vector3(0f, 0f, 1.5f), new Vector3(0f, 0f, 25.25f), new Vector3(19.5f, 0f, 25.25f) })
        {
            var t = new GameObject("Waypoint" + wps.Count).transform;
            t.SetParent(flowGo.transform, false);
            t.position = p;
            wps.Add(t);
        }
        var flow = flowGo.AddComponent<FlowingArrowLights>();
        flow.waypoints = wps.ToArray();
        flow.segmentMaterial = mChevron;
        flow.Rebuild();
        var aoi = flowGo.AddComponent<Sho_GuideSignAOI>();
        aoi.signId = "E_flow"; aoi.designType = "flowing_arrow";
        // 視線判定用の薄い当たり（トリガー）
        AoiStrip(flowGo.transform, new Vector3(0f, 0.03f, 13.4f), new Vector3(0.6f, 0.06f, 23.8f));
        AoiStrip(flowGo.transform, new Vector3(9.75f, 0.03f, 25.25f), new Vector3(19.5f, 0.06f, 0.6f));

        return groups;
    }

    static void AoiStrip(Transform parent, Vector3 center, Vector3 size)
    {
        var go = new GameObject("AOI_Strip");
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        var bc = go.AddComponent<BoxCollider>();
        bc.size = size;
        bc.isTrigger = true;
    }

    static void FloorArrow(Transform parent, int i, Vector3 pos, Vector3 dir)
    {
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        q.name = $"B_FloorArrow_{i:00}";
        Object.DestroyImmediate(q.GetComponent<Collider>());
        q.transform.SetParent(parent, false);
        q.transform.position = pos + Vector3.up * 0.015f;
        q.transform.rotation = Quaternion.LookRotation(Vector3.down, dir);
        q.transform.localScale = new Vector3(0.8f, 0.8f, 1f);
        var r = q.GetComponent<Renderer>();
        r.sharedMaterial = mArrow;
        r.shadowCastingMode = ShadowCastingMode.Off;
        var bc = q.AddComponent<BoxCollider>();
        bc.size = new Vector3(1f, 1f, 0.05f);
        bc.isTrigger = true;
        var fb = q.AddComponent<FlashingBeacon>();
        fb.pattern = FlashingBeacon.Pattern.Steady;
        fb.color = new Color(0.75f, 1f, 0.55f);
        fb.brightness = 1f;
        var aoi = q.AddComponent<Sho_GuideSignAOI>();
        aoi.signId = q.name; aoi.designType = "floor_arrow";
    }

    static void BuildSigns(Transform parent, string prefix, FlashingBeacon.Pattern pattern, string design)
    {
        // (名前, 位置, 看板の表が向く方向, 素材)
        var list = new (string n, Vector3 p, Vector3 face, Material m)[]
        {
            ("S1_StartCorridor", new Vector3(0f, 2.35f, 12f), Vector3.back, mSignUp),
            ("S2_Junction", new Vector3(0f, 2.1f, 26.48f), Vector3.back, mSignRight),
            ("S3_EastCorridor", new Vector3(5f, 2.35f, 25.25f), Vector3.left, mSignUp),
            ("S4_SideJunction", new Vector3(13f, 2.35f, 25.25f), Vector3.left, mSignUp),
            ("S5_ExitDoor", new Vector3(19.97f, 2.4f, 25.25f), Vector3.left, mSignExit),
        };
        int k = 0;
        foreach (var s in list)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = $"{prefix}_{s.n}";
            Object.DestroyImmediate(q.GetComponent<Collider>());
            q.transform.SetParent(parent, false);
            q.transform.position = s.p;
            q.transform.rotation = Quaternion.LookRotation(-s.face, Vector3.up); // Quad の表は -Z 側
            q.transform.localScale = new Vector3(0.8f, 0.4f, 1f);
            var r = q.GetComponent<Renderer>();
            r.sharedMaterial = s.m;
            r.shadowCastingMode = ShadowCastingMode.Off;
            var bc = q.AddComponent<BoxCollider>();
            bc.size = new Vector3(1f, 1f, 0.05f);
            bc.isTrigger = true;

            // 看板の後ろの箱（吊り下げ看板らしく）
            var back = Box("Housing", q.transform, s.p - s.face * 0.04f, new Vector3(0.86f, 0.46f, 0.06f), mWall, false);
            back.transform.rotation = q.transform.rotation;
            back.transform.localScale = new Vector3(1.07f, 1.15f, 0.06f);

            var lgo = new GameObject("Light");
            lgo.transform.SetParent(q.transform, false);
            lgo.transform.position = s.p + s.face * 0.3f;
            var light = lgo.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 3.5f;
            light.shadows = LightShadows.None;

            var fb = q.AddComponent<FlashingBeacon>();
            fb.targetRenderers = new[] { r };
            fb.targetLight = light;
            fb.pattern = pattern;
            fb.frequencyHz = 1.0f;
            fb.dutyCycle = 0.5f;
            fb.phaseOffset = 0f;
            fb.color = Color.white;   // 看板の絵の色をそのまま見せる
            fb.brightness = 1.2f;
            fb.offLevel = pattern == FlashingBeacon.Pattern.Steady ? 1f : 0.25f;
            fb.lightIntensity = 1.5f;

            var aoi = q.AddComponent<Sho_GuideSignAOI>();
            aoi.signId = q.name; aoi.designType = design;
            k++;
        }
    }
}
#endif
