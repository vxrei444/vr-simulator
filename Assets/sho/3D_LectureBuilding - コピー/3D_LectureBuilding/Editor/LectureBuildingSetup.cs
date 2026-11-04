#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// MagicaVoxel から書き出した 3D_LectureBuilding（OBJ チャンク群）を Unity / Quest 用にセットアップする。
///   1. パレット画像(256x1)を Point / 非圧縮 / ミップマップなし に設定
///   2. URP 用マテリアル LectureBuilding_Palette.mat を作成
///   3. 全 OBJ のインポート設定（スケール1, コライダー生成, マテリアル自動生成なし）
///   4. フォルダごとにプレハブを作成（Prefabs/LectureBuilding_corridor.prefab など）
/// メニュー: Tools > LectureBuilding > セットアップ
/// このスクリプトは 3D_LectureBuilding/Editor/ に置くこと（場所から自動でフォルダを判定する）。
/// </summary>
[InitializeOnLoad]
public static class LectureBuildingSetup
{
    const string MatName = "LectureBuilding_Palette.mat";
    const string PrefabDir = "Prefabs";
    const string AutoRunKey = "LectureBuildingSetup_AutoAsked";

    static LectureBuildingSetup()
    {
        // 初回だけ、プレハブが無ければセットアップするか聞く
        EditorApplication.delayCall += () =>
        {
            string root = FindRoot();
            if (root == null || SessionState.GetBool(AutoRunKey, false)) return;
            SessionState.SetBool(AutoRunKey, true);
            if (AssetDatabase.IsValidFolder($"{root}/{PrefabDir}")) return;
            if (EditorUtility.DisplayDialog("3D_LectureBuilding",
                    "講義棟モデルのセットアップ（マテリアル・コライダー・プレハブ作成）を実行しますか？\n" +
                    "あとから Tools > LectureBuilding > セットアップ でも実行できます。", "実行", "あとで"))
                Run();
        };
    }

    [MenuItem("Tools/LectureBuilding/セットアップ")]
    public static void Run()
    {
        string root = FindRoot();
        if (root == null) { Debug.LogError("[LectureBuilding] 3D_LectureBuilding フォルダが見つかりません"); return; }

        // ---- 1. パレット ----
        string palettePath = AssetDatabase.FindAssets("t:Texture2D", new[] { root })
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => p.EndsWith(".png") && !p.Contains($"/{PrefabDir}/"));
        if (palettePath == null) { Debug.LogError("[LectureBuilding] パレット PNG が見つかりません"); return; }
        var ti = (TextureImporter)AssetImporter.GetAtPath(palettePath);
        ti.textureType = TextureImporterType.Default;
        ti.filterMode = FilterMode.Point;
        ti.mipmapEnabled = false;
        ti.wrapMode = TextureWrapMode.Clamp;
        ti.npotScale = TextureImporterNPOTScale.None;
        ti.textureCompression = TextureImporterCompression.Uncompressed;
        ti.sRGBTexture = true;
        ti.SaveAndReimport();
        var palette = AssetDatabase.LoadAssetAtPath<Texture2D>(palettePath);

        // ---- 2. マテリアル ----
        string matDir = $"{root}/Materials";
        if (!AssetDatabase.IsValidFolder(matDir)) AssetDatabase.CreateFolder(root, "Materials");
        string matPath = $"{matDir}/{MatName}";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        var shader = Shader.Find("Universal Render Pipeline/Simple Lit")
                     ?? Shader.Find("Universal Render Pipeline/Lit")
                     ?? Shader.Find("Standard");
        if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, matPath); }
        mat.shader = shader;
        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", palette);
        if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", palette);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0f);
        mat.enableInstancing = true;
        EditorUtility.SetDirty(mat);

        // ---- 3. OBJ インポート設定 ----
        var objs = AssetDatabase.FindAssets("t:Model", new[] { root })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => p.EndsWith(".obj", System.StringComparison.OrdinalIgnoreCase))
            .ToList();
        try
        {
            AssetDatabase.StartAssetEditing();
            int i = 0;
            foreach (var p in objs)
            {
                if (i++ % 20 == 0) EditorUtility.DisplayProgressBar("LectureBuilding", "インポート設定 " + Path.GetFileName(p), (float)i / objs.Count);
                var mi = (ModelImporter)AssetImporter.GetAtPath(p);
                bool changed = mi.globalScale != 1f || !mi.addCollider ||
                               mi.materialImportMode != ModelImporterMaterialImportMode.None || mi.isReadable;
                if (!changed) continue;
                mi.globalScale = 1f;                       // 1 unit = 1 m（ボクセル 0.1m）
                mi.addCollider = true;                     // 歩けるよう MeshCollider を生成
                mi.materialImportMode = ModelImporterMaterialImportMode.None;
                mi.isReadable = false;
                mi.importAnimation = false;
                mi.animationType = ModelImporterAnimationType.None;
                mi.importCameras = false;
                mi.importLights = false;
                mi.SaveAndReimport();
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            EditorUtility.ClearProgressBar();
        }

        // ---- 4. プレハブ（フォルダごと） ----
        string prefabDir = $"{root}/{PrefabDir}";
        if (!AssetDatabase.IsValidFolder(prefabDir)) AssetDatabase.CreateFolder(root, PrefabDir);
        var groups = objs.GroupBy(p => Path.GetDirectoryName(p).Replace('\\', '/'));
        var made = new List<string>();
        foreach (var g in groups)
        {
            string groupName = Path.GetFileName(g.Key);
            var rootGo = new GameObject(groupName);
            foreach (var p in g.OrderBy(NaturalKey))
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                if (model == null) continue;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
                PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                go.name = Path.GetFileNameWithoutExtension(p);
                go.transform.SetParent(rootGo.transform, false); // 座標は OBJ に焼き込み済み
                foreach (var r in go.GetComponentsInChildren<MeshRenderer>())
                    r.sharedMaterials = Enumerable.Repeat(mat, Mathf.Max(1, r.sharedMaterials.Length)).ToArray();
                foreach (var t in go.GetComponentsInChildren<Transform>())
                    GameObjectUtility.SetStaticEditorFlags(t.gameObject,
                        StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
            }
            string path = $"{prefabDir}/{groupName}.prefab";
            PrefabUtility.SaveAsPrefabAsset(rootGo, path);
            Object.DestroyImmediate(rootGo);
            made.Add(path);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[LectureBuilding] 完了: OBJ {objs.Count} 個 / プレハブ {made.Count} 個\n" + string.Join("\n", made));
        EditorUtility.DisplayDialog("3D_LectureBuilding", $"セットアップ完了\nプレハブ: {root}/{PrefabDir}/ にあります", "OK");
    }

    // このスクリプトの場所（.../3D_LectureBuilding/Editor/）から親フォルダを求める
    static string FindRoot()
    {
        foreach (var guid in AssetDatabase.FindAssets("LectureBuildingSetup t:MonoScript"))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            string dir = Path.GetDirectoryName(Path.GetDirectoryName(p))?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && AssetDatabase.IsValidFolder(dir)) return dir;
        }
        return null;
    }

    static string NaturalKey(string p) =>
        Regex.Replace(Path.GetFileNameWithoutExtension(p), @"\d+", m => m.Value.PadLeft(6, '0'));
}
#endif
