using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// デモ全体の進行管理。
/// スタート待ち → (スタートのドア通過) 計測中 → (ゴールのドア通過) 完了・結果表示
/// ・画面表示は OnGUI なので Canvas / TextMeshPro の設定は不要
/// ・START / GOAL と一定間隔の位置を CSV に保存（Python 分析にそのまま使える形式）
/// ・結果画面で R キーを押すと最初からやり直し
/// </summary>
public class DemoManager : MonoBehaviour
{
    public static DemoManager Instance { get; private set; }
    public enum State { Ready, Running, Finished }

    [Header("参照（空なら自動で探します）")]
    public DemoPlayerController player;

    [Header("ログ")]
    [Tooltip("画面とCSVに出す条件名（A〜D）")]
    public string conditionLabel = "A";
    [Tooltip("位置を記録する間隔（秒）")]
    public float positionLogInterval = 0.5f;
    public bool saveCsv = true;

    [Header("表示")]
    [Tooltip("START / GOAL の大きな文字を出す秒数")]
    public float bannerDuration = 2f;

    public State CurrentState { get; private set; } = State.Ready;

    class LogEntry
    {
        public string clock;   // 実時刻
        public float t;        // スタートからの経過秒
        public string evt;     // START / GOAL / POS
        public Vector3 pos;
    }

    readonly List<LogEntry> events = new List<LogEntry>();
    readonly List<LogEntry> track = new List<LogEntry>();

    float startTime, goalTime, nextPosLog;
    float totalDistance;
    string bannerText; Color bannerColor; float bannerUntil;
    string savedPath;
    Vector3 initialPos; Quaternion initialRot;

    public float Elapsed =>
        CurrentState == State.Running ? Time.time - startTime :
        CurrentState == State.Finished ? goalTime - startTime : 0f;

    void Awake()
    {
        Instance = this;
        if (player == null) player = FindFirstObjectByType<DemoPlayerController>();
    }

    void Start()
    {
        if (player != null)
        {
            initialPos = player.transform.position;
            initialRot = player.transform.rotation;
        }
    }

    // ───────── ドアから呼ばれる ─────────

    public void OnStartDoor()
    {
        if (CurrentState != State.Ready) return;   // 2回目以降の通過は無視
        startTime = Time.time;
        CurrentState = State.Running;
        events.Add(MakeEntry("START", 0f));
        nextPosLog = Time.time;
        ShowBanner("START", new Color(0.3f, 1f, 0.45f));
        Debug.Log($"[Demo] START  {events[0].clock}");
    }

    public void OnGoalDoor()
    {
        if (CurrentState != State.Running) return; // スタート前のゴールは無視
        goalTime = Time.time;
        CurrentState = State.Finished;
        events.Add(MakeEntry("GOAL", goalTime - startTime));
        ShowBanner("GOAL", new Color(1f, 0.6f, 0.15f));

        totalDistance = CalcDistance();
        if (player != null) { player.canMove = false; player.LockCursor(false); }
        if (saveCsv) SaveCsv();
        Debug.Log($"[Demo] GOAL  {events[events.Count - 1].clock}  タイム {FormatTime(Elapsed)}");
    }

    // ───────── 進行 ─────────

    void Update()
    {
        if (CurrentState == State.Running && Time.time >= nextPosLog)
        {
            track.Add(MakeEntry("POS", Time.time - startTime));
            nextPosLog = Time.time + positionLogInterval;
        }

        if (CurrentState == State.Finished && RestartPressed()) ResetDemo();
    }

    public void ResetDemo()
    {
        events.Clear();
        track.Clear();
        savedPath = null;
        bannerUntil = 0f;
        CurrentState = State.Ready;
        if (player != null)
        {
            player.ResetTo(initialPos, initialRot);
            player.canMove = true;
            player.LockCursor(true);
        }
    }

    bool RestartPressed()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.R);
#endif
    }

    LogEntry MakeEntry(string evt, float t) => new LogEntry
    {
        clock = DateTime.Now.ToString("HH:mm:ss.fff"),
        t = t,
        evt = evt,
        pos = player != null ? player.transform.position : Vector3.zero
    };

    void ShowBanner(string text, Color color)
    {
        bannerText = text;
        bannerColor = color;
        bannerUntil = Time.time + bannerDuration;
    }

    float CalcDistance()
    {
        var pts = AllEntries().Select(e => e.pos).ToList();
        float d = 0f;
        for (int i = 1; i < pts.Count; i++)
        {
            Vector3 a = pts[i - 1], b = pts[i];
            a.y = 0f; b.y = 0f;                    // 水平距離
            d += Vector3.Distance(a, b);
        }
        return d;
    }

    IEnumerable<LogEntry> AllEntries() => events.Concat(track).OrderBy(e => e.t); // 安定ソート

    void SaveCsv()
    {
        try
        {
            string dir = Path.Combine(Application.persistentDataPath, "Logs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"demo_cond{conditionLabel}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            var sb = new StringBuilder();
            sb.AppendLine("condition,clock,elapsed_sec,event,x,y,z");
            foreach (var e in AllEntries())
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0},{1},{2:F3},{3},{4:F3},{5:F3},{6:F3}",
                    conditionLabel, e.clock, e.t, e.evt, e.pos.x, e.pos.y, e.pos.z));
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            savedPath = path;
            Debug.Log($"[Demo] CSV保存: {path}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Demo] CSV保存に失敗: {ex.Message}");
        }
    }

    static string FormatTime(float sec)
    {
        int m = (int)(sec / 60f);
        float s = sec - m * 60;
        return $"{m:00}:{s:00.00}";
    }

    // ───────── 画面表示 ─────────

    GUIStyle hud, banner, title, big, cell, small;
    int styledHeight;

    void EnsureStyles()
    {
        if (hud != null && styledHeight == Screen.height) return;
        styledHeight = Screen.height;
        float s = Screen.height / 1080f;

        GUIStyle Make(int size, FontStyle fs = FontStyle.Normal, TextAnchor a = TextAnchor.MiddleLeft)
        {
            var st = new GUIStyle(GUI.skin.label)
            { fontSize = Mathf.Max(10, Mathf.RoundToInt(size * s)), fontStyle = fs, alignment = a, wordWrap = true };
            st.normal.textColor = Color.white;
            return st;
        }

        hud = Make(30, FontStyle.Bold);
        banner = Make(180, FontStyle.Bold, TextAnchor.MiddleCenter);
        title = Make(52, FontStyle.Bold, TextAnchor.MiddleCenter);
        big = Make(72, FontStyle.Bold, TextAnchor.MiddleCenter);
        cell = Make(30);
        small = Make(20);
        small.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
    }

    static void DrawRect(Rect r, Color c)
    {
        var old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }

    void OnGUI()
    {
        EnsureStyles();
        float s = Screen.height / 1080f;

        // 左上の状態表示
        string stateText = CurrentState switch
        {
            State.Ready => "スタートのドアへ向かってください",
            State.Running => "計測中",
            _ => "ゴール！"
        };
        DrawRect(new Rect(20 * s, 20 * s, 680 * s, 110 * s), new Color(0, 0, 0, 0.55f));
        GUI.Label(new Rect(40 * s, 25 * s, 650 * s, 50 * s), $"条件{conditionLabel}　{stateText}", hud);
        GUI.Label(new Rect(40 * s, 72 * s, 650 * s, 50 * s), $"タイム  {FormatTime(Elapsed)}", hud);

        // START / GOAL の大きな文字（最後の0.5秒でフェード）
        if (Time.time < bannerUntil)
        {
            float a = Mathf.Clamp01((bannerUntil - Time.time) / 0.5f);
            DrawRect(new Rect(0, Screen.height * 0.35f, Screen.width, Screen.height * 0.3f), new Color(0, 0, 0, 0.45f * a));
            var c = bannerColor; c.a = a;
            banner.normal.textColor = c;
            GUI.Label(new Rect(0, 0, Screen.width, Screen.height), bannerText, banner);
        }

        // 結果画面（GOAL表示が消えたら出す）
        if (CurrentState == State.Finished && Time.time >= bannerUntil) DrawResults(s);
    }

    void DrawResults(float s)
    {
        float w = Screen.width * 0.72f, h = Screen.height * 0.72f;
        var panel = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
        DrawRect(new Rect(0, 0, Screen.width, Screen.height), new Color(0, 0, 0, 0.35f));
        DrawRect(panel, new Color(0.08f, 0.09f, 0.12f, 0.92f));

        float pad = 40 * s;
        float x = panel.x + pad, iw = panel.width - pad * 2;
        float y = panel.y + pad * 0.6f;

        GUI.Label(new Rect(x, y, iw, 70 * s), $"結果（条件{conditionLabel}）", title); y += 75 * s;
        GUI.Label(new Rect(x, y, iw, 95 * s), $"タイム  {FormatTime(Elapsed)}", big); y += 95 * s;
        GUI.Label(new Rect(x, y, iw, 45 * s),
            $"移動距離 {totalDistance:F1} m　／　位置の記録 {track.Count} 点（{positionLogInterval:0.##}秒ごと）",
            new GUIStyle(cell) { alignment = TextAnchor.MiddleCenter });
        y += 70 * s;

        // ログ表
        float[] col = { 0f, 0.2f, 0.47f, 0.68f };
        string[] head = { "イベント", "時刻", "経過", "位置 (x, z)" };
        DrawRect(new Rect(x, y, iw, 50 * s), new Color(1, 1, 1, 0.1f));
        for (int i = 0; i < head.Length; i++)
            GUI.Label(new Rect(x + iw * col[i] + 15 * s, y, iw * 0.3f, 50 * s), head[i], cell);
        y += 55 * s;

        foreach (var e in events)
        {
            string[] row =
            {
                e.evt, e.clock, FormatTime(e.t),
                $"({e.pos.x:F1}, {e.pos.z:F1})"
            };
            for (int i = 0; i < row.Length; i++)
                GUI.Label(new Rect(x + iw * col[i] + 15 * s, y, iw * 0.3f, 50 * s), row[i], cell);
            y += 52 * s;
        }

        // フッター
        float fy = panel.yMax - pad * 0.6f - 70 * s;
        if (!string.IsNullOrEmpty(savedPath))
            GUI.Label(new Rect(x, fy, iw, 35 * s), $"ログ保存先: {savedPath}", small);
        GUI.Label(new Rect(x, fy + 35 * s, iw, 35 * s), "R キーでもう一度", small);
    }
}
