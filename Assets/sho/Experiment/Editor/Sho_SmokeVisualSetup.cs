#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 今開いているシーンの煙を「リアルな見た目」にする（testScene・実験場どちらでも可）。
/// メニュー: Tools > 避難実験 > 煙の見た目をリアルにする（今のシーン）
///   ・SmokeLayerController の黒い箱をやめ、Sho_SmokeLayerVisual（まだらの板＋煙の粒）に切り替え
///   ・煙の色を灰〜茶系に
///   ・照明と誘導看板に、煙の中で光がにじむ輪（Sho_SmokeLightHalo）を付ける
///   ・火元（FireVisual / FireSource）に、立ち上る煙と天井に広がる煙（Sho_SmokePlume）を付ける
/// 元に戻すとき: SmokeSystem の Use Box Volumes をオンにし、SmokeVisual を削除する。
/// </summary>
public static class Sho_SmokeVisualSetup
{
    const string Gen = "Assets/sho/Experiment/Generated";
    static readonly Color SmokeColor = new Color(0.16f, 0.15f, 0.14f, 1f);

    [MenuItem("Tools/避難実験/煙の見た目をリアルにする（今のシーン）")]
    public static void Run()
    {
        var smoke = Object.FindAnyObjectByType<SmokeLayerController>();
        if (smoke == null)
        {
            EditorUtility.DisplayDialog("煙の見た目", "このシーンに SmokeSystem（SmokeLayerController）がありません。", "OK");
            return;
        }
        if (!AssetDatabase.IsValidFolder(Gen)) { Directory.CreateDirectory(Gen); AssetDatabase.Refresh(); }

        // ---- 素材 ----
        var sliceShader = Shader.Find("Sho/SmokeSlice");
        if (sliceShader == null)
        {
            EditorUtility.DisplayDialog("煙の見た目", "シェーダー Sho/SmokeSlice が見つかりません。\nAssets/sho/Experiment/Shaders/Sho_SmokeSlice.shader が読み込まれているか、Console のエラーを確認してください。", "OK");
            return;
        }
        var sliceMat = LoadOrCreate("SmokeSlice", sliceShader);
        sliceMat.SetColor("_BaseColor", SmokeColor);
        sliceMat.renderQueue = (int)RenderQueue.Transparent;
        EditorUtility.SetDirty(sliceMat);

        var puff = AssetDatabase.LoadAssetAtPath<Texture2D>(Gen + "/SmokePuff.png") ?? SaveTex("SmokePuff", 128, PuffPixel);
        var particleMat = AssetDatabase.LoadAssetAtPath<Material>(Gen + "/SmokeParticle.mat");
        if (particleMat == null) particleMat = ParticleMat("SmokeParticle", puff, new Color(0.15f, 0.14f, 0.13f, 0.5f), false);
        particleMat.SetColor("_BaseColor", new Color(0.15f, 0.14f, 0.13f, 0.5f));
        EditorUtility.SetDirty(particleMat);

        var haloTex = SaveTex("SmokeHalo", 128, HaloPixel);
        var haloMat = ParticleMat("SmokeHalo", haloTex, Color.white, true);

        // ---- 煙層の見た目 ----
        Undo.RecordObject(smoke, "Smoke visual");
        smoke.useBoxVolumes = false;
        smoke.smokeColor = SmokeColor;
        foreach (var r in smoke.regions) if (r.volume != null) r.volume.gameObject.SetActive(false);
        EditorUtility.SetDirty(smoke);

        var old = GameObject.Find("SmokeVisual");
        if (old != null) Undo.DestroyObjectImmediate(old);
        var vgo = new GameObject("SmokeVisual");
        Undo.RegisterCreatedObjectUndo(vgo, "SmokeVisual");
        var vis = vgo.AddComponent<Sho_SmokeLayerVisual>();
        vis.smoke = smoke;
        vis.sliceMaterial = sliceMat;
        vis.particleMaterial = particleMat;
        vis.smokeColor = SmokeColor;
        vis.sliceCount = 8;
        vis.sliceSpanM = 1.3f;
        vis.bottomAlpha = 0.12f;

        // ---- 光の輪 ----
        int halos = 0;
        foreach (var b in Object.FindObjectsByType<FlashingBeacon>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            // 光源付きの看板（光る看板・点滅灯）だけ。床の矢印などには付けない
            if (b.targetLight == null && b.GetComponentInChildren<Light>(true) == null) continue;
            if (AddHalo(b.transform, smoke, haloMat, null, b, 0.25f, 1.8f)) halos++;
        }
        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (l.type == LightType.Directional) continue;
            if (l.GetComponentInParent<FlashingBeacon>(true) != null) continue;        // 看板側で付けた
            if (l.GetComponentInParent<FireVisual>(true) != null) continue;           // 火の光は除く
            if (l.GetComponentInParent<Sho_SmokePlume>(true) != null) continue;
            if (AddHalo(l.transform, smoke, haloMat, l, null, 0.3f, 2.5f)) halos++;
        }

        // ---- 火元の煙 ----
        int plumes = 0;
        foreach (var fv in Object.FindObjectsByType<FireVisual>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            Vector3 basePos = fv.flames != null ? fv.flames.transform.position : fv.transform.position;
            if (AddPlume(fv.transform, basePos, smoke, particleMat)) plumes++;
        }
        var fsType = System.Type.GetType("FireSource, Assembly-CSharp"); // 炎のスクリプトがあれば
        if (fsType != null)
            foreach (var o in Object.FindObjectsByType(fsType, FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var c = o as Component;
                if (c != null && AddPlume(c.transform, c.transform.position, smoke, particleMat)) plumes++;
            }

        EditorSceneManager.MarkSceneDirty(smoke.gameObject.scene);
        Selection.activeGameObject = vgo;
        EditorUtility.DisplayDialog("煙の見た目",
            $"煙の見た目を切り替えました。\n\n・煙層：まだらの板 {vis.sliceCount} 枚＋境目の煙の粒\n・光の輪：{halos} か所\n・火元の煙：{plumes} か所\n\n" +
            "細かい調整は Hierarchy の SmokeVisual で行えます。\n元に戻すとき：SmokeSystem の Use Box Volumes をオンにし、SmokeVisual を削除。\nシーンを保存（Ctrl+S）してください。", "OK");
    }

    [MenuItem("Tools/避難実験/煙の見た目：立体の霧にする（今のシーン）")]
    public static void RunVolume()
    {
        var smoke = Object.FindAnyObjectByType<SmokeLayerController>();
        if (smoke == null) { EditorUtility.DisplayDialog("煙の見た目", "このシーンに SmokeSystem（SmokeLayerController）がありません。", "OK"); return; }
        var volShader = Shader.Find("Sho/SmokeVolume");
        if (volShader == null) { EditorUtility.DisplayDialog("煙の見た目", "シェーダー Sho/SmokeVolume が見つかりません。Console のエラーを確認してください。", "OK"); return; }

        // 光の輪・火元の煙・板の素材など（まだなら）を先に用意
        if (GameObject.Find("SmokeVisual") == null) Run();
        float upm = Mathf.Max(0.01f, smoke.worldUnitsPerMeter);

        // 3D ノイズ
        var noise = MakeNoise3D("SmokeNoise3D", 32);
        var volMat = LoadOrCreate("SmokeVolume3D", volShader);
        volMat.SetTexture("_NoiseTex", noise);
        volMat.SetColor("_BaseColor", SmokeColor);
        volMat.renderQueue = (int)RenderQueue.Transparent + 10;
        EditorUtility.SetDirty(volMat);

        // 煙の粒：ソフトパーティクル（壁にめり込んだ切れ目を消す）＋カメラ直前で薄くする
        var particleMat = AssetDatabase.LoadAssetAtPath<Material>(Gen + "/SmokeParticle.mat");
        if (particleMat != null)
        {
            float far = 1.0f * upm, cn = 0.3f * upm, cf = 1.2f * upm;
            particleMat.SetFloat("_SoftParticlesEnabled", 1f);
            particleMat.SetFloat("_SoftParticlesNearFadeDistance", 0f);
            particleMat.SetFloat("_SoftParticlesFarFadeDistance", far);
            particleMat.SetVector("_SoftParticleFadeParams", new Vector4(0f, 1f / far, 0f, 0f));
            particleMat.EnableKeyword("_SOFTPARTICLES_ON");
            particleMat.SetFloat("_CameraFadingEnabled", 1f);
            particleMat.SetFloat("_CameraNearFadeDistance", cn);
            particleMat.SetFloat("_CameraFarFadeDistance", cf);
            particleMat.SetVector("_CameraFadeParams", new Vector4(cn, 1f / (cf - cn), 0f, 0f));
            particleMat.EnableKeyword("_FADING_ON");
            EditorUtility.SetDirty(particleMat);
        }

        // カメラの Depth Texture をオン（立体の霧・ソフトパーティクルに必要）
        foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
        {
            var data = cam.GetUniversalAdditionalCameraData();
            if (data == null) continue;
            Undo.RecordObject(data, "Depth texture");
            data.requiresDepthOption = CameraOverrideOption.On;
            EditorUtility.SetDirty(data);
        }

        // 火元から広がるようにする
        Undo.RecordObject(smoke, "Spread");
        smoke.spatialSpread = true;
        if (smoke.smokeOrigin == null)
        {
            var fv = Object.FindAnyObjectByType<FireVisual>();
            if (fv != null) smoke.smokeOrigin = fv.transform;
        }
        EditorUtility.SetDirty(smoke);

        // 立体の霧
        var old = GameObject.Find("SmokeVolumeVisual");
        if (old != null) Undo.DestroyObjectImmediate(old);
        var go = new GameObject("SmokeVolumeVisual");
        Undo.RegisterCreatedObjectUndo(go, "SmokeVolumeVisual");
        var v = go.AddComponent<Sho_SmokeVolumeVisual>();
        v.smoke = smoke; v.volumeMaterial = volMat; v.particleMaterial = particleMat; v.smokeColor = SmokeColor;

        // 板の重ね（SmokeVisual）は比較用に残して非表示
        var slices = GameObject.Find("SmokeVisual");
        if (slices != null)
        {
            var lv = slices.GetComponent<Sho_SmokeLayerVisual>();
            if (lv != null) { Undo.RecordObject(lv, "Slices 8"); lv.sliceCount = 8; lv.sliceSpanM = 1.3f; lv.bottomAlpha = 0.12f; EditorUtility.SetDirty(lv); }
            Undo.RecordObject(slices, "Hide slices"); slices.SetActive(false);
        }

        // 重さの計測
        var perf = Object.FindAnyObjectByType<Sho_PerfMonitor>(FindObjectsInactive.Include);
        if (perf == null)
        {
            var pgo = new GameObject("PerfMonitor");
            Undo.RegisterCreatedObjectUndo(pgo, "PerfMonitor");
            perf = pgo.AddComponent<Sho_PerfMonitor>();
        }
        // 固定中心窩レンダリング
        var fov = Object.FindAnyObjectByType<Sho_FoveatedRendering>(FindObjectsInactive.Include);
        if (fov == null) fov = Undo.AddComponent<Sho_FoveatedRendering>(perf.gameObject);
        fov.level = Sho_FoveatedRendering.Level.High;
        perf.smoke = smoke; perf.volumeVisual = v; perf.foveation = fov;
        perf.sliceVisual = slices != null ? slices.GetComponent<Sho_SmokeLayerVisual>() : null;

        EditorSceneManager.MarkSceneDirty(smoke.gameObject.scene);
        Selection.activeGameObject = go;
        EditorUtility.DisplayDialog("煙の見た目",
            "立体の霧に切り替えました。\n\n・煙は火元から広がり、遠いほど遅れて薄く届きます（症状の計算にも反映）\n" +
            "・煙層の中にいるときは立体の霧のサンプル数を自動で減らします\n・固定中心窩レンダリング（High）を有効にします（Quest 実機で有効）\n" +
            "・板の重ね（SmokeVisual）は比較用に非表示で残しています\n・PerfMonitor が重さを CSV に記録します（画面表示なし。F5〜F8 で見た目を切り替えて比較）\n\n" +
            "シーンを保存（Ctrl+S）してください。", "OK");
    }

    static Texture3D MakeNoise3D(string name, int n)
    {
        string path = $"{Gen}/{name}.asset";
        var existing = AssetDatabase.LoadAssetAtPath<Texture3D>(path);
        if (existing != null) return existing;
        var tex = new Texture3D(n, n, n, TextureFormat.R8, false) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
        var c = new Color[n * n * n];
        for (int z = 0; z < n; z++)
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = x / (float)n, v = y / (float)n, w = z / (float)n;
                    float val = 0.55f * PNoise(u * 4, v * 4, w * 4, 4) + 0.3f * PNoise(u * 8, v * 8, w * 8, 8) + 0.15f * PNoise(u * 16, v * 16, w * 16, 16);
                    c[x + y * n + z * n * n] = new Color(val, val, val, 1f);
                }
        tex.SetPixels(c);
        tex.Apply();
        AssetDatabase.CreateAsset(tex, path);
        return tex;
    }

    // 周期的（つなぎ目のない）値ノイズ
    static float PNoise(float x, float y, float z, int p)
    {
        int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y), zi = Mathf.FloorToInt(z);
        float fx = x - xi, fy = y - yi, fz = z - zi;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
        float L(int a, int b, int d) => Hash(((a % p) + p) % p, ((b % p) + p) % p, ((d % p) + p) % p);
        float x00 = Mathf.Lerp(L(xi, yi, zi), L(xi + 1, yi, zi), fx);
        float x10 = Mathf.Lerp(L(xi, yi + 1, zi), L(xi + 1, yi + 1, zi), fx);
        float x01 = Mathf.Lerp(L(xi, yi, zi + 1), L(xi + 1, yi, zi + 1), fx);
        float x11 = Mathf.Lerp(L(xi, yi + 1, zi + 1), L(xi + 1, yi + 1, zi + 1), fx);
        return Mathf.Lerp(Mathf.Lerp(x00, x10, fy), Mathf.Lerp(x01, x11, fy), fz);
    }

    static float Hash(int x, int y, int z)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + z * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0xFFFFFF;
        }
    }

    static bool AddHalo(Transform parent, SmokeLayerController smoke, Material mat, Light light, FlashingBeacon beacon, float minM, float maxM)
    {
        if (parent.Find("SmokeHalo") != null) return false;
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        q.name = "SmokeHalo";
        Object.DestroyImmediate(q.GetComponent<Collider>());
        Undo.RegisterCreatedObjectUndo(q, "Halo");
        q.transform.SetParent(parent, false);
        q.transform.localPosition = beacon != null ? new Vector3(0f, 0f, -0.05f) : Vector3.zero; // 看板の少し手前
        var r = q.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.enabled = false;
        var h = q.AddComponent<Sho_SmokeLightHalo>();
        h.smoke = smoke; h.sourceLight = light; h.beacon = beacon; h.haloRenderer = r;
        h.minSizeM = minM; h.maxSizeM = maxM;
        return true;
    }

    static bool AddPlume(Transform parent, Vector3 basePos, SmokeLayerController smoke, Material mat)
    {
        if (parent.GetComponentInChildren<Sho_SmokePlume>(true) != null) return false;
        float upm = Mathf.Max(0.01f, smoke.worldUnitsPerMeter);
        var root = new GameObject("SmokePlume");
        Undo.RegisterCreatedObjectUndo(root, "Plume");
        root.transform.SetParent(parent, true);
        root.transform.position = basePos;

        // 立ち上る煙
        var pgo = new GameObject("Plume");
        pgo.transform.SetParent(root.transform, false);
        pgo.transform.position = basePos + Vector3.up * 0.3f * upm;
        pgo.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
        var plume = pgo.AddComponent<ParticleSystem>();
        var m = plume.main;
        m.startLifetime = new ParticleSystem.MinMaxCurve(2.5f, 3.5f);
        m.startSpeed = new ParticleSystem.MinMaxCurve(0.8f * upm, 1.3f * upm);
        m.startSize = new ParticleSystem.MinMaxCurve(0.5f * upm, 0.9f * upm);
        m.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        m.startColor = new Color(0.12f, 0.11f, 0.1f, 0.6f);
        m.maxParticles = 80;
        m.simulationSpace = ParticleSystemSimulationSpace.World;
        var sh = plume.shape; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 8f; sh.radius = 0.35f * upm;
        var sol = plume.sizeOverLifetime; sol.enabled = true; sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 2.2f));
        SetFade(plume);
        var pr = pgo.GetComponent<ParticleSystemRenderer>(); pr.sharedMaterial = mat; pr.shadowCastingMode = ShadowCastingMode.Off;

        // 天井に沿って広がる煙
        var jgo = new GameObject("CeilingJet");
        jgo.transform.SetParent(root.transform, false);
        jgo.transform.position = new Vector3(basePos.x, smoke.CeilingYAt(basePos.y + 0.1f) - 0.25f * upm, basePos.z);
        jgo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        var jet = jgo.AddComponent<ParticleSystem>();
        var jm = jet.main;
        jm.startLifetime = new ParticleSystem.MinMaxCurve(6f, 9f);
        jm.startSpeed = 0f;
        jm.startSize = new ParticleSystem.MinMaxCurve(1.2f * upm, 2.2f * upm);
        jm.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        jm.startColor = new Color(0.13f, 0.12f, 0.11f, 0.5f);
        jm.maxParticles = 90;
        jm.simulationSpace = ParticleSystemSimulationSpace.World;
        var jsh = jet.shape; jsh.shapeType = ParticleSystemShapeType.Circle; jsh.radius = 0.5f * upm;
        var vel = jet.velocityOverLifetime;
        vel.enabled = true;
        vel.space = ParticleSystemSimulationSpace.Local;
        vel.x = new ParticleSystem.MinMaxCurve(0f); vel.y = new ParticleSystem.MinMaxCurve(0f); vel.z = new ParticleSystem.MinMaxCurve(0f);
        vel.radial = new ParticleSystem.MinMaxCurve(0.8f * upm);   // 外側へ広がる
        var jsol = jet.sizeOverLifetime; jsol.enabled = true; jsol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.7f, 1f, 1.8f));
        SetFade(jet);
        var jr = jgo.GetComponent<ParticleSystemRenderer>(); jr.sharedMaterial = mat; jr.shadowCastingMode = ShadowCastingMode.Off;

        var sp = root.AddComponent<Sho_SmokePlume>();
        sp.smoke = smoke; sp.plume = plume; sp.ceilingJet = jet;
        return true;
    }

    static void SetFade(ParticleSystem ps)
    {
        var col = ps.colorOverLifetime; col.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                  new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0.8f, 0.7f), new GradientAlphaKey(0f, 1f) });
        col.color = g;
    }

    // ---------- 素材づくり ----------
    static Material LoadOrCreate(string name, Shader shader)
    {
        string path = $"{Gen}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) { m = new Material(shader); AssetDatabase.CreateAsset(m, path); }
        else m.shader = shader;
        return m;
    }

    static Material ParticleMat(string name, Texture tex, Color c, bool additive)
    {
        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        var m = LoadOrCreate(name, shader);
        m.SetTexture("_BaseMap", tex); m.SetColor("_BaseColor", c);
        m.SetFloat("_Surface", 1f);
        m.SetFloat("_Blend", additive ? 2f : 0f);
        m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", additive ? (float)BlendMode.One : (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0f);
        m.SetOverrideTag("RenderType", "Transparent");
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        if (additive) m.EnableKeyword("_BLENDMODE_ADD"); else m.DisableKeyword("_BLENDMODE_ADD");
        m.renderQueue = (int)RenderQueue.Transparent;
        EditorUtility.SetDirty(m);
        return m;
    }

    delegate Color PixelFn(float u, float v);

    static Texture2D SaveTex(string name, int size, PixelFn fn)
    {
        string path = $"{Gen}/{name}.png";
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                px[y * size + x] = fn((x + 0.5f) / size, (y + 0.5f) / size);
        tex.SetPixels(px);
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        ti.alphaIsTransparency = true; ti.wrapMode = TextureWrapMode.Clamp; ti.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    static Color HaloPixel(float u, float v)
    {
        float dx = (u - 0.5f) * 2f, dy = (v - 0.5f) * 2f;
        float r = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy));
        float a = Mathf.Pow(1f - r, 2.5f);
        return new Color(1f, 1f, 1f, a);
    }

    static Color PuffPixel(float u, float v)
    {
        float dx = (u - 0.5f) * 2f, dy = (v - 0.5f) * 2f;
        float r = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy));
        float n = Mathf.PerlinNoise(u * 6f, v * 6f) * 0.6f + Mathf.PerlinNoise(u * 13f + 7f, v * 13f) * 0.4f;
        return new Color(1f, 1f, 1f, Mathf.Pow(1f - r, 2f) * Mathf.Lerp(0.5f, 1f, n));
    }
}
#endif
