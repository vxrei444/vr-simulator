using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// ゴール（または時間切れ）したら、少し待ってから結果シーンへ移動する。
/// EvacuationSessionManager のイベントを使うので、GoalZone は書き換えなくてよい。
/// シーンの空オブジェクト（例: SessionManager と同じオブジェクト）に付ける。
/// </summary>
public class GoToResultScene : MonoBehaviour
{
    [Tooltip("移動先のシーン名（Build Profiles のシーンリストに入れておくこと）")]
    public string resultSceneName = "ResultScene";

    [Tooltip("ゴールしてから移動するまでの秒数（ログ保存・メッセージ表示の時間）")]
    public float delaySeconds = 2f;

    [Tooltip("時間切れのときも結果シーンへ移動する")]
    public bool alsoOnTimeout = true;

    // 結果シーンで表示に使えるよう、結果を保存しておく
    public static float LastEscapeTime;
    public static string LastGoalName = "";
    public static bool LastReachedGoal;

    void Start()
    {
        var m = EvacuationSessionManager.Instance;
        if (m == null)
        {
            Debug.LogError("[GoToResultScene] シーンに EvacuationSessionManager がありません");
            return;
        }

        m.onSessionFinished.AddListener(time =>
        {
            LastEscapeTime = time;
            LastGoalName = m.ReachedGoalName;
            LastReachedGoal = true;
            StartCoroutine(LoadAfterDelay());
        });

        if (alsoOnTimeout)
        {
            m.onSessionTimedOut.AddListener(() =>
            {
                LastEscapeTime = m.ElapsedTime;
                LastGoalName = "";
                LastReachedGoal = false;
                StartCoroutine(LoadAfterDelay());
            });
        }
    }

    IEnumerator LoadAfterDelay()
    {
        yield return new WaitForSeconds(delaySeconds);
        SceneManager.LoadScene(resultSceneName);
    }
}