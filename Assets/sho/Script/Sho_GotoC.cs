using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 踏むシーン移動する。
/// ・当たり判定は自動で用意する（上に乗っても、通り抜けても反応する）
/// ・エディタでは Build Profiles に登録しなくても移動できる
/// </summary>
public class Sho_Goto : MonoBehaviour
{
    const string SceneName = "実験場C";   // 移動先のシーン名
    public string playerTag = "Player";
    public float delaySeconds = 0.5f;
    public Color pressedColor = new Color(0.2f, 0.8f, 0.3f);

    static bool loading;

    void Awake()
    {
        loading = false;
        // 上に乗れる床はそのまま残し、少し上にはみ出す「反応用の当たり判定」を追加する
        var trigger = gameObject.AddComponent<BoxCollider>();
        trigger.isTrigger = true;
        trigger.center = new Vector3(0f, 0.5f, 0f);
        trigger.size = new Vector3(1f, 2f, 1f);
    }

    void OnTriggerEnter(Collider other)
    {
        if (loading || !other.CompareTag(playerTag)) return;
        loading = true;
        var rend = GetComponent<Renderer>();
        if (rend != null) rend.material.color = pressedColor;
        Debug.Log($"[{GetType().Name}] {SceneName} へ移動");
        StartCoroutine(Go());
    }

    IEnumerator Go()
    {
        if (delaySeconds > 0f) yield return new WaitForSeconds(delaySeconds);
#if UNITY_EDITOR
        
        foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:Scene " + SceneName))
        {
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            if (System.IO.Path.GetFileNameWithoutExtension(path) == SceneName)
            {
                UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Single));
                yield break;
            }
        }
        Debug.LogError($"[{GetType().Name}] シーン「{SceneName}」が見つかりません");
#else
        
        SceneManager.LoadScene(SceneName);
#endif
    }
}