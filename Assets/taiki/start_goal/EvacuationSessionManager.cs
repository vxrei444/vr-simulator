using System;
using System.IO;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

/// <summary>
/// 避難セッション全体の状態（待機→計測中→ゴール/時間切れ）を管理する。
/// シーンに1つだけ置く。StartZone / GoalZone から呼ばれる。
/// </summary>
public enum EvacuationState { Waiting, Running, Finished, TimedOut }

public class EvacuationSessionManager : MonoBehaviour
{
    public static EvacuationSessionManager Instance { get; private set; }

    [Header("実験条件（ログに記録される）")]
    public string participantId = "P00";
    [Tooltip("A: 目印なし / B: 床矢印 / C: 光る看板 / D: 動的矢印")]
    public string conditionId = "A";

    [Header("制限時間（秒）。0以下で無制限")]
    public float timeLimit = 300f;

    [Header("結果CSVのファイル名（persistentDataPath に保存）")]
    public string resultFileName = "evacuation_results.csv";

    [Header("イベント（ログ記録・UI表示などをここに繋ぐ）")]
    public UnityEvent onSessionStarted;
    public UnityEvent<float> onSessionFinished;   // 引数: 脱出時間(秒)
    public UnityEvent onSessionTimedOut;

    public EvacuationState State { get; private set; } = EvacuationState.Waiting;
    public DateTime SessionStartDateTime { get; private set; }
    public string ReachedGoalName { get; private set; } = "";

    /// <summary>計測中は現在の経過時間、終了後は確定タイムを返す</summary>
    public float ElapsedTime => State == EvacuationState.Running ? Time.time - startTime : finalTime;

    float startTime;
    float finalTime;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[Session] EvacuationSessionManager がシーンに複数あります。");
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void Update()
    {
        if (State == EvacuationState.Running && timeLimit > 0f && Time.time - startTime >= timeLimit)
        {
            EndSession(EvacuationState.TimedOut, "");
        }

#if ENABLE_LEGACY_INPUT_MANAGER
        // エディタでのデバッグ用（ヘッドセットなしで確認するとき）
        if (Input.GetKeyDown(KeyCode.S)) StartSession();
        if (Input.GetKeyDown(KeyCode.G)) ReachGoal("DebugKey");
        if (Input.GetKeyDown(KeyCode.R)) RestartScene();
#endif
    }

    /// <summary>計測開始。待機中のときだけ有効（多重スタート防止）</summary>
    public void StartSession()
    {
        if (State != EvacuationState.Waiting) return;

        startTime = Time.time;
        SessionStartDateTime = DateTime.Now;
        State = EvacuationState.Running;

        Debug.Log($"[Session] START  participant={participantId} condition={conditionId}");
        onSessionStarted?.Invoke();
    }

    /// <summary>ゴール到達。計測中のときだけ有効（多重ゴール防止）</summary>
    public void ReachGoal(string goalName)
    {
        if (State != EvacuationState.Running) return;
        EndSession(EvacuationState.Finished, goalName);
    }

    public void RestartScene()
    {
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    void EndSession(EvacuationState endState, string goalName)
    {
        finalTime = Time.time - startTime;
        State = endState;
        ReachedGoalName = goalName;

        WriteResult();

        if (endState == EvacuationState.Finished)
        {
            Debug.Log($"[Session] GOAL  exit={goalName} time={finalTime:F2}s");
            onSessionFinished?.Invoke(finalTime);
        }
        else
        {
            Debug.Log($"[Session] TIME OUT  ({timeLimit}s)");
            onSessionTimedOut?.Invoke();
        }
    }

    /// <summary>1プレイ1行で結果をCSVに追記する（Python分析用）</summary>
    void WriteResult()
    {
        try
        {
            string path = Path.Combine(Application.persistentDataPath, resultFileName);
            bool isNew = !File.Exists(path);
            using (var w = new StreamWriter(path, append: true))
            {
                if (isNew)
                    w.WriteLine("start_datetime,participant_id,condition,result,escape_time_sec,goal_name");

                string result = State == EvacuationState.Finished ? "goal" : "timeout";
                w.WriteLine($"{SessionStartDateTime:yyyy-MM-dd HH:mm:ss},{participantId},{conditionId},{result},{finalTime:F3},{ReachedGoalName}");
            }
            Debug.Log($"[Session] 結果を保存: {path}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Session] 結果CSVの書き込みに失敗: {e.Message}");
        }
    }
}
