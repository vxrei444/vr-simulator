using System;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// 【taiki】指定した秒数ごとにプレイヤーの位置座標を取得し、CSVファイル（＋任意でConsole）に出力する。
/// ・他のメンバーのスクリプトと名前がぶつからないよう、クラス名・関数名・変数名に「taiki_」を付けている。
/// ・Meta XR (OVRCameraRig) 使用時は taiki_target に CenterEyeAnchor を指定するのが基本（頭の位置＝実際の視点位置）。
/// ・スタート／ゴール判定から taiki_StartLogging() / taiki_StopLogging() を呼べば、計測区間だけ記録できる。
/// ・[FormerlySerializedAs] は、リネーム前にインスペクタで設定した値を引き継ぐためのもの。
/// </summary>
public class taiki_PositionLogger : MonoBehaviour
{
    [Header("記録対象")]
    [Tooltip("位置を記録するTransform。未設定なら Camera.main を使う（OVRCameraRig なら CenterEyeAnchor）")]
    [FormerlySerializedAs("target")]
    [SerializeField] private Transform taiki_target;

    [Header("記録設定")]
    [Tooltip("何秒ごとに記録するか")]
    [Min(0.01f)]
    [FormerlySerializedAs("intervalSeconds")]
    [SerializeField] private float taiki_intervalSeconds = 0.5f;

    [Tooltip("実験条件（A/B/C/D）。ファイル名と各行に書き込まれる")]
    [FormerlySerializedAs("conditionLabel")]
    [SerializeField] private string taiki_conditionLabel = "A";

    [Tooltip("シーン開始と同時に記録を始めるか（falseならtaiki_StartLogging()を呼ぶまで待つ）")]
    [FormerlySerializedAs("startOnAwake")]
    [SerializeField] private bool taiki_startOnAwake = false;

    [Tooltip("Consoleにも出力するか（デバッグ用。本番はOFF推奨）")]
    [FormerlySerializedAs("alsoDebugLog")]
    [SerializeField] private bool taiki_alsoDebugLog = true;

    [Tooltip("何行ごとにファイルへ書き込みを確定させるか")]
    [FormerlySerializedAs("flushEveryNRows")]
    [SerializeField] private int taiki_flushEveryNRows = 10;

    public bool taiki_IsLogging { get; private set; }
    public string taiki_CurrentFilePath { get; private set; }

    private StreamWriter taiki_writer;
    private float taiki_timer;
    private float taiki_startTime;
    private int taiki_rowCount;
    private string taiki_sessionId;

    // ※ Awake / Start / Update / OnApplicationPause / OnApplicationQuit / OnDestroy は
    //   Unity が名前で呼び出す関数なので、taiki_ を付けずにそのままにしている。
    private void Awake()
    {
        if (taiki_target == null && Camera.main != null)
        {
            taiki_target = Camera.main.transform;
        }
    }

    private void Start()
    {
        if (taiki_startOnAwake) taiki_StartLogging();
    }

    /// <summary>記録開始（スタート地点の判定から呼ぶ想定）</summary>
    public void taiki_StartLogging()
    {
        if (taiki_IsLogging) return;
        if (taiki_target == null)
        {
            Debug.LogError("[taiki_PositionLogger] taiki_target が設定されていません");
            return;
        }

        taiki_sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string taiki_dir = Path.Combine(Application.persistentDataPath, "Logs");
        Directory.CreateDirectory(taiki_dir);
        taiki_CurrentFilePath = Path.Combine(taiki_dir, $"position_{taiki_conditionLabel}_{taiki_sessionId}.csv");

        taiki_writer = new StreamWriter(taiki_CurrentFilePath, false, new System.Text.UTF8Encoding(false));
        taiki_writer.WriteLine("session_id,condition,elapsed_sec,x,y,z,yaw_deg");

        taiki_startTime = Time.time;
        taiki_timer = 0f;
        taiki_rowCount = 0;
        taiki_IsLogging = true;

        taiki_WriteRow(); // 開始時点（0秒）も記録
        Debug.Log($"[taiki_PositionLogger] 記録開始: {taiki_CurrentFilePath}");
    }

    /// <summary>記録終了（ゴール判定から呼ぶ想定）</summary>
    public void taiki_StopLogging()
    {
        if (!taiki_IsLogging) return;

        taiki_WriteRow(); // ゴール到達時点も記録
        taiki_IsLogging = false;
        taiki_CloseWriter();
        Debug.Log($"[taiki_PositionLogger] 記録終了: {taiki_rowCount}行 → {taiki_CurrentFilePath}");
    }

    private void Update()
    {
        if (!taiki_IsLogging) return;

        taiki_timer += Time.deltaTime;
        // フレーム落ちで interval を複数回またいでも取りこぼさないよう while で回す
        while (taiki_timer >= taiki_intervalSeconds)
        {
            taiki_timer -= taiki_intervalSeconds;
            taiki_WriteRow();
        }
    }

    private void taiki_WriteRow()
    {
        if (taiki_writer == null) return;

        Vector3 taiki_pos = taiki_target.position;
        float taiki_yaw = taiki_target.eulerAngles.y;
        float taiki_elapsed = Time.time - taiki_startTime;

        // 小数点を必ず「.」にする（端末の言語設定でカンマになるのを防ぐ）
        string taiki_line = string.Format(CultureInfo.InvariantCulture,
            "{0},{1},{2:F3},{3:F3},{4:F3},{5:F3},{6:F1}",
            taiki_sessionId, taiki_conditionLabel, taiki_elapsed, taiki_pos.x, taiki_pos.y, taiki_pos.z, taiki_yaw);

        taiki_writer.WriteLine(taiki_line);
        taiki_rowCount++;

        if (taiki_rowCount % taiki_flushEveryNRows == 0) taiki_writer.Flush();
        if (taiki_alsoDebugLog) Debug.Log($"[taiki_PositionLogger] {taiki_line}");
    }

    private void taiki_CloseWriter()
    {
        if (taiki_writer == null) return;
        taiki_writer.Flush();
        taiki_writer.Close();
        taiki_writer = null;
    }

    // Questはヘッドセットを外すとポーズされ、そのまま終了されることがあるので保存しておく
    private void OnApplicationPause(bool paused)
    {
        if (paused) taiki_writer?.Flush();
    }

    private void OnApplicationQuit() => taiki_StopLogging();
    private void OnDestroy() => taiki_StopLogging();
}
