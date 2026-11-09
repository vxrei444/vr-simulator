using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 避難中の行動ログ（軌跡・頭の向き・視線先・移動速度）を記録する。
/// EvacuationSessionManager の開始/ゴール/時間切れイベントに自動で連動する。
///
/// 出力先: Application.persistentDataPath/EvacLogs/
///   {日時}_{参加者}_{条件}_track.csv  … 0.1秒ごとのサンプル
///   {日時}_{参加者}_{条件}_events.csv … 看板注視・分岐点通過などのイベント
/// Quest 実機では /sdcard/Android/data/{パッケージ名}/files/EvacLogs/ に保存される。
///
/// 使い方: シーンの空オブジェクトに付け、head に CenterEyeAnchor、
///         body に PlayerBodyCollider のオブジェクトを入れる。
/// </summary>
/// <summary>
/// ログ（track.csv）に列を追加したいスクリプトが実装するインターフェース。
/// 例: SmokeExposure が「煙層の高さ・頭の位置の煙の濃さ・症状の段階」を追加する。
/// </summary>
public interface IEvacLogColumns
{
    string LogHeader { get; }   // "a,b,c"
    string LogValues { get; }   // "1,2,3"（LogHeader と同じ数）
}

public class EvacuationTrajectoryLogger : MonoBehaviour
{
    public static EvacuationTrajectoryLogger Instance { get; private set; }

    [Header("参照")]
    [Tooltip("OVRCameraRig > TrackingSpace > CenterEyeAnchor（未設定なら Main Camera）")]
    public Transform head;
    [Tooltip("床位置の基準になる体（PlayerBodyCollider）。未設定なら head の真下を使う")]
    public Transform body;

    [Header("記録設定")]
    [Tooltip("サンプリング間隔(秒)。0.1 = 10Hz")]
    public float sampleInterval = 0.1f;
    [Tooltip("視線レイの最大距離(m)")]
    public float gazeRayDistance = 30f;
    [Tooltip("視線レイが当たる対象のレイヤー")]
    public LayerMask gazeLayers = ~0;
    [Tooltip("看板を『見た』と判定する連続注視時間(秒)")]
    public float signFixationThreshold = 0.3f;

    readonly StringBuilder track = new StringBuilder(1 << 16);
    readonly StringBuilder events = new StringBuilder(1 << 12);
    string trackPath, eventsPath;
    bool recording;
    float nextSampleTime;
    Vector3 lastPos;
    float lastSampleTime;

    // 看板注視の連続判定用
    GuideSignAOI currentSign;
    float currentSignSince;
    bool currentSignReported;

    readonly RaycastHit[] hits = new RaycastHit[16];
    readonly List<IEvacLogColumns> extraColumns = new List<IEvacLogColumns>();
    static readonly CultureInfo IC = CultureInfo.InvariantCulture;

    void Awake()
    {
        Instance = this;
        if (head == null && Camera.main != null) head = Camera.main.transform;
    }

    void Start()
    {
        var m = EvacuationSessionManager.Instance;
        if (m == null) { Debug.LogError("[TrajLogger] EvacuationSessionManager がありません"); return; }
        m.onSessionStarted.AddListener(BeginRecording);
        m.onSessionFinished.AddListener(t => EndRecording("goal", t));
        m.onSessionTimedOut.AddListener(() => EndRecording("timeout", m.ElapsedTime));

        // StartZone が OnSceneLoad モードだと、このStartより先に計測が始まっている場合がある
        if (m.State == EvacuationState.Running) BeginRecording();
    }

    void BeginRecording()
    {
        if (recording) return;
        var m = EvacuationSessionManager.Instance;
        string dir = Path.Combine(Application.persistentDataPath, "EvacLogs");
        Directory.CreateDirectory(dir);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string baseName = $"{stamp}_{Safe(m.participantId)}_{Safe(m.conditionId)}";
        trackPath = Path.Combine(dir, baseName + "_track.csv");
        eventsPath = Path.Combine(dir, baseName + "_events.csv");

        track.Clear();
        extraColumns.Clear();
        foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            if (mb is IEvacLogColumns c && mb.isActiveAndEnabled) extraColumns.Add(c);
        track.Append("t,pos_x,pos_y,pos_z,head_y,yaw_deg,pitch_deg,gaze_x,gaze_y,gaze_z,speed_mps,gaze_target,gaze_sign_id,gaze_sign_design,gaze_dist");
        foreach (var c in extraColumns) track.Append(',').Append(c.LogHeader);
        track.AppendLine();
        events.Clear();
        events.AppendLine("t,event,id,detail,pos_x,pos_z");

        recording = true;
        lastPos = FootPos();
        lastSampleTime = 0f;
        nextSampleTime = 0f;
        LogEvent("session_start", m.participantId, m.conditionId);
        Debug.Log($"[TrajLogger] 記録開始 {trackPath}");
    }

    void EndRecording(string result, float time)
    {
        if (!recording) return;
        Sample(); // 最終位置
        LogEvent("session_end", result, time.ToString("F3", IC));
        recording = false;
        Flush();
    }

    void Update()
    {
        if (!recording) return;
        float t = EvacuationSessionManager.Instance.ElapsedTime;
        if (t >= nextSampleTime)
        {
            Sample();
            nextSampleTime = t + sampleInterval;
        }
    }

    Vector3 FootPos()
    {
        if (body != null) return body.position;
        return head != null ? head.position : transform.position;
    }

    void Sample()
    {
        if (head == null) return;
        float t = EvacuationSessionManager.Instance.ElapsedTime;
        Vector3 pos = FootPos();
        Vector3 fwd = head.forward;
        Vector3 e = head.rotation.eulerAngles;
        float yaw = e.y;                                   // 0=+Z(北), 90=+X(東)
        float pitch = e.x > 180f ? e.x - 360f : e.x;       // 下向きが＋

        float dt = t - lastSampleTime;
        Vector3 d = pos - lastPos; d.y = 0f;
        float speed = dt > 1e-4f ? d.magnitude / dt : 0f;
        lastPos = pos; lastSampleTime = t;

        string targetName = "", signId = "", signDesign = "";
        float dist = -1f;
        GuideSignAOI sign = null;
        // トリガーも拾うが、看板(AOI)以外のトリガー（Start/Goal/分岐点ゾーン）は透過させる
        int n = Physics.RaycastNonAlloc(head.position, fwd, hits, gazeRayDistance, gazeLayers, QueryTriggerInteraction.Collide);
        float best = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var h = hits[i];
            if (h.distance >= best || h.collider.transform.IsChildOf(body != null ? body : head)) continue;
            var s = h.collider.GetComponentInParent<GuideSignAOI>();
            if (h.collider.isTrigger && s == null) continue;
            best = h.distance; sign = s;
            targetName = h.collider.gameObject.name; dist = h.distance;
        }
        if (sign != null) { signId = sign.signId; signDesign = sign.designType; }
        UpdateSignFixation(sign, t);

        track.Append(t.ToString("F3", IC)).Append(',')
             .Append(F(pos.x)).Append(',').Append(F(pos.y)).Append(',').Append(F(pos.z)).Append(',')
             .Append(F(head.position.y)).Append(',')
             .Append(F(yaw)).Append(',').Append(F(pitch)).Append(',')
             .Append(F(fwd.x)).Append(',').Append(F(fwd.y)).Append(',').Append(F(fwd.z)).Append(',')
             .Append(F(speed)).Append(',')
             .Append(Csv(targetName)).Append(',').Append(Csv(signId)).Append(',').Append(Csv(signDesign)).Append(',')
             .Append(F(dist));
        foreach (var c in extraColumns) track.Append(',').Append(c.LogValues);
        track.AppendLine();

        // 長時間のプレイでもデータを失わないよう、定期的に書き出す
        if (track.Length > 200_000) Flush();
    }

    void UpdateSignFixation(GuideSignAOI sign, float t)
    {
        if (sign != currentSign)
        {
            if (currentSign != null && currentSignReported)
                LogEvent("sign_gaze_end", currentSign.signId, (t - currentSignSince).ToString("F2", IC));
            currentSign = sign;
            currentSignSince = t;
            currentSignReported = false;
        }
        else if (sign != null && !currentSignReported && t - currentSignSince >= signFixationThreshold)
        {
            currentSignReported = true;
            LogEvent("sign_gaze_start", sign.signId, sign.designType);
        }
    }

    /// <summary>ほかのスクリプト（DecisionPointZone など）から任意のイベントを記録する</summary>
    public void LogEvent(string ev, string id, string detail = "")
    {
        if (!recording) return;
        float t = EvacuationSessionManager.Instance.ElapsedTime;
        Vector3 p = FootPos();
        events.Append(t.ToString("F3", IC)).Append(',').Append(Csv(ev)).Append(',').Append(Csv(id)).Append(',')
              .Append(Csv(detail)).Append(',').Append(F(p.x)).Append(',').Append(F(p.z)).AppendLine();
    }

    void Flush()
    {
        try
        {
            if (trackPath == null) return;
            File.AppendAllText(trackPath, track.ToString(), Encoding.UTF8);
            File.AppendAllText(eventsPath, events.ToString(), Encoding.UTF8);
            track.Clear(); events.Clear();
        }
        catch (Exception ex) { Debug.LogError($"[TrajLogger] 書き込み失敗: {ex.Message}"); }
    }

    // Quest はアプリを閉じる前にバックグラウンドに回るので、そこでも保存する
    void OnApplicationPause(bool paused) { if (paused && recording) Flush(); }
    void OnApplicationQuit() { if (recording) { LogEvent("session_end", "quit"); Flush(); } }

    static string F(float v) => v.ToString("F3", IC);
    static string Safe(string s) => string.IsNullOrEmpty(s) ? "NA" : string.Join("_", s.Split(Path.GetInvalidFileNameChars()));
    static string Csv(string s) => string.IsNullOrEmpty(s) ? "" : (s.Contains(",") || s.Contains("\"") ? "\"" + s.Replace("\"", "\"\"") + "\"" : s);
}
