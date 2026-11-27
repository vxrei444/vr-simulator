using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 入ると「実験場A」へ移動するゾーン。Cube の Box Collider の Is Trigger をオンにしておく。
/// </summary>
public class Sho_GotoA : MonoBehaviour
{
    const string SceneName = "実験場A";
    public string playerTag = "Player";
    bool loading;

    void OnTriggerEnter(Collider other)
    {
        if (loading || !other.CompareTag(playerTag)) return;
        loading = true;
#if UNITY_EDITOR
        // エディタ：Build Profiles に登録しなくても、シーンのファイルを探して開く
        foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:Scene " + SceneName))
        {
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            if (System.IO.Path.GetFileNameWithoutExtension(path) == SceneName)
            {
                UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Single));
                return;
            }
        }
        Debug.LogError("シーン「" + SceneName + "」が見つかりません");
#else
        SceneManager.LoadScene(SceneName); // 実機では Build Profiles への登録が必要
#endif
    }
}