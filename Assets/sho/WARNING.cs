using UnityEngine;
using TMPro; // TextMeshProを使うために必要

public class WARNING : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI warningText; // 画面のテキストを割り当てる用
    [SerializeField] private float displayDuration = 5.0f; // 表示し続ける時間（秒）

    void Start()
    {
        // ゲームスタート時にテキストを表示する
        if (warningText != null)
        {
            warningText.gameObject.SetActive(true);
        }

        // 指定した秒数（5秒後）に文字を消すメソッドを呼び出す
        Invoke("HideText", displayDuration);
    }

    void HideText()
    {
        if (warningText != null)
        {
            warningText.gameObject.SetActive(false);
        }
    }
}