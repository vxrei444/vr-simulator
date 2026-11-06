using System;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// 指定した秒数ごとにプレイヤーの位置座標を取得し、CSVファイル（＋任意でConsole）に出力する。
/// ・Meta XR (OVRCameraRig) 使用時は target に CenterEyeAnchor を指定するのが基本（頭の位置＝実際の視点位置）。
/// ・スタート／ゴール判定から StartLogging() / StopLogging() を呼べば、計測区間だけ記録できる。
/// </summary>
public class PositionLogger : MonoBehaviour
{
    [Header("記録対象")]
    [Tooltip("位置を記録するTransform。未設定なら Camera.main を使う（OVRCameraRig なら CenterEyeAnchor）")]
    [SerializeField] private Transform target;

    [Header("記録設定")]
    [Tooltip("何秒ごとに記録するか")]
    [Min(0.01f)]
    [SerializeField] private float intervalSeconds = 0.5f;

    [Tooltip("実験条件（A/B/C/D）。ファイル名と各行に書き込まれる")]
    [SerializeField] private string conditionLabel = "A";

    [Tooltip("シーン開始と同時に記録を始めるか（falseならStartLogging()を呼ぶまで待つ）")]
    [SerializeField] private bool startOnAwake = false;

    [Tooltip("Consoleにも出力するか（デバッグ用。本番はOFF推奨）")]
    [SerializeField] private bool alsoDebugLog = true;

    [Tooltip("何行ごとにファイルへ書き込みを確定させるか")]
    [SerializeField] private int flushEveryNRows = 10;

    public bool IsLogging { get; private set; }
    public string CurrentFilePath { get; private set; }

    private StreamWriter writer;
    private float timer;
    private float startTime;
    private int rowCount;
    private string sessionId;

    private void Awake()
    {
        if (target == null && Camera.main != null)
        {
            target = Camera.main.transform;
        }
    }

    private void Start()
    {
        if (startOnAwake) StartLogging();
    }

    /// <summary>記録開始（スタート地点の判定から呼ぶ想定）</summary>
    public void StartLogging()
    {
        if (IsLogging) return;
        if (target == null)
        {
            Debug.LogError("[PositionLogger] target が設定されていません");
            return;
        }

        sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string dir = Path.Combine(Application.persistentDataPath, "Logs");
        Directory.CreateDirectory(dir);
        CurrentFilePath = Path.Combine(dir, $"position_{conditionLabel}_{sessionId}.csv");

        writer = new StreamWriter(CurrentFilePath, false, new System.Text.UTF8Encoding(false));
        writer.WriteLine("session_id,condition,elapsed_sec,x,y,z,yaw_deg");

        startTime = Time.time;
        timer = 0f;
        rowCount = 0;
        IsLogging = true;

        WriteRow(); // 開始時点（0秒）も記録
        Debug.Log($"[PositionLogger] 記録開始: {CurrentFilePath}");
    }

    /// <summary>記録終了（ゴール判定から呼ぶ想定）</summary>
    public void StopLogging()
    {
        if (!IsLogging) return;

        WriteRow(); // ゴール到達時点も記録
        IsLogging = false;
        CloseWriter();
        Debug.Log($"[PositionLogger] 記録終了: {rowCount}行 → {CurrentFilePath}");
    }

    private void Update()
    {
        if (!IsLogging) return;

        timer += Time.deltaTime;
        // フレーム落ちで interval を複数回またいでも取りこぼさないよう while で回す
        while (timer >= intervalSeconds)
        {
            timer -= intervalSeconds;
            WriteRow();
        }
    }

    private void WriteRow()
    {
        if (writer == null) return;

        Vector3 p = target.position;
        float yaw = target.eulerAngles.y;
        float elapsed = Time.time - startTime;

        // 小数点を必ず「.」にする（端末の言語設定でカンマになるのを防ぐ）
        string line = string.Format(CultureInfo.InvariantCulture,
            "{0},{1},{2:F3},{3:F3},{4:F3},{5:F3},{6:F1}",
            sessionId, conditionLabel, elapsed, p.x, p.y, p.z, yaw);

        writer.WriteLine(line);
        rowCount++;

        if (rowCount % flushEveryNRows == 0) writer.Flush();
        if (alsoDebugLog) Debug.Log($"[PositionLogger] {line}");
    }

    private void CloseWriter()
    {
        if (writer == null) return;
        writer.Flush();
        writer.Close();
        writer = null;
    }

    // Questはヘッドセットを外すとポーズされ、そのまま終了されることがあるので保存しておく
    private void OnApplicationPause(bool paused)
    {
        if (paused) writer?.Flush();
    }

    private void OnApplicationQuit() => StopLogging();
    private void OnDestroy() => StopLogging();
}
