using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

public class PlayerDirectionLogger : MonoBehaviour
{
    [Header("観測判定設定")]
    [SerializeField] private string targetTag = "GazeTarget";

    // 連続して観測する必要がある時間(秒)
    [SerializeField] private float requiredGazeTime = 3.0f;

    [Header("視界・Ray設定")]
    [SerializeField] private float rayDistance = 10.0f;
    [SerializeField] private float detectionRadius = 0.15f;
    [SerializeField] private LayerMask raycastMask = ~0;

    [Header("CSV出力設定")]
    [SerializeField] private bool enableCsv = true;

    // 時系列ログ(位置・向き・視線ターゲット)を記録する間隔(秒)。0以下で毎フレーム
    [SerializeField] private float sampleInterval = 0.1f;

    private Transform currentTarget;
    private float gazeStartTime;
    private bool hasLogged = false;

    // 対象が破棄されても記録できるよう、情報を保持しておく
    private string currentTargetName;
    private string currentTargetTag;
    private Vector3 currentTargetPosition;

    private StreamWriter sampleWriter;      // 時系列ログ
    private StreamWriter observationWriter; // 観測イベントログ
    private float nextSampleTime;

    void OnEnable()
    {
        if (enableCsv)
        {
            OpenCsvFiles();
        }
    }

    void Update()
    {
        CheckGazeTarget();
        WriteSample();
    }

    void OnDisable()
    {
        // 観測中に停止した場合も終了ログを出す
        if (currentTarget != null)
        {
            EndObservation();
            currentTarget = null;
        }

        CloseCsvFiles();
    }

    // ---------------------------------------------------------
    // CSV 関連
    // ---------------------------------------------------------

    void OpenCsvFiles()
    {
        try
        {
            string dir = Application.persistentDataPath;
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            string samplePath = Path.Combine(dir, "gaze_samples_" + stamp + ".csv");
            string obsPath = Path.Combine(dir, "gaze_observations_" + stamp + ".csv");

            // Excelで文字化けしないよう BOM付きUTF-8
            var enc = new UTF8Encoding(true);

            sampleWriter = new StreamWriter(samplePath, false, enc) { AutoFlush = true };
            sampleWriter.WriteLine(
                "DateTime,Time," +
                "HeadPosX,HeadPosY,HeadPosZ," +
                "ForwardX,ForwardY,ForwardZ," +
                "TargetName,TargetTag,TargetPosX,TargetPosY,TargetPosZ");

            observationWriter = new StreamWriter(obsPath, false, enc) { AutoFlush = true };
            observationWriter.WriteLine(
                "StartDateTime,StartTime,EndTime,Duration,Reached," +
                "TargetName,TargetTag,TargetPosX,TargetPosY,TargetPosZ," +
                "HeadPosX,HeadPosY,HeadPosZ,ForwardX,ForwardY,ForwardZ");

            Debug.Log("CSV出力先: " + dir);
        }
        catch (Exception e)
        {
            Debug.LogError("CSVファイルを開けませんでした: " + e.Message);
            sampleWriter = null;
            observationWriter = null;
        }
    }

    void CloseCsvFiles()
    {
        if (sampleWriter != null)
        {
            sampleWriter.Close();
            sampleWriter = null;
        }

        if (observationWriter != null)
        {
            observationWriter.Close();
            observationWriter = null;
        }
    }

    // 時系列ログ:一定間隔で「位置・向き・視線ターゲット」を1行追記
    void WriteSample()
    {
        if (sampleWriter == null) return;

        if (sampleInterval > 0f && Time.time < nextSampleTime) return;
        nextSampleTime = Time.time + sampleInterval;

        Vector3 pos = transform.position;
        Vector3 fwd = transform.forward;

        string targetName = currentTarget != null ? currentTargetName : "";
        string targetTagStr = currentTarget != null ? currentTargetTag : "";
        string targetPos = currentTarget != null ? Vec3(currentTargetPosition) : ",,";

        sampleWriter.WriteLine(
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "," +
            F(Time.time) + "," +
            Vec3(pos) + "," +
            Vec3(fwd) + "," +
            Esc(targetName) + "," +
            Esc(targetTagStr) + "," +
            targetPos);
    }

    // 観測イベントログ:観測が終わったときに1行追記
    void WriteObservation(float endTime, float duration, bool reached)
    {
        if (observationWriter == null) return;

        observationWriter.WriteLine(
            observationStartDateTime + "," +
            F(gazeStartTime) + "," +
            F(endTime) + "," +
            F(duration) + "," +
            (reached ? "True" : "False") + "," +
            Esc(currentTargetName) + "," +
            Esc(currentTargetTag) + "," +
            Vec3(currentTargetPosition) + "," +
            Vec3(transform.position) + "," +
            Vec3(transform.forward));
    }

    private string observationStartDateTime = "";

    static string F(float v)
    {
        return v.ToString("F3", CultureInfo.InvariantCulture);
    }

    static string Vec3(Vector3 v)
    {
        return F(v.x) + "," + F(v.y) + "," + F(v.z);
    }

    // CSVで壊れないよう、カンマや引用符を含む文字列をエスケープ
    static string Esc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
        {
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        return s;
    }

    // ---------------------------------------------------------
    // 視線判定(元のロジックと同じ)
    // ---------------------------------------------------------

    void CheckGazeTarget()
    {
        Transform hitTarget = FindGazeTarget();

        if (hitTarget != currentTarget)
        {
            if (currentTarget != null)
            {
                EndObservation();
            }

            currentTarget = hitTarget;

            if (currentTarget != null)
            {
                gazeStartTime = Time.time;
                hasLogged = false;
                currentTargetName = currentTarget.name;
                currentTargetTag = currentTarget.tag;
                observationStartDateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                Debug.Log("観測開始: " + currentTargetName);
            }
        }

        if (currentTarget == null)
        {
            return;
        }

        // 対象の最新位置を保持(動く対象にも対応)
        currentTargetPosition = currentTarget.position;

        float gazeDuration = Time.time - gazeStartTime;

        if (gazeDuration >= requiredGazeTime && !hasLogged)
        {
            RecordObservation(gazeDuration);
            hasLogged = true;
        }
    }

    Transform FindGazeTarget()
    {
        Ray ray = new Ray(transform.position, transform.forward);

        RaycastHit[] hits = Physics.SphereCastAll(
            ray,
            detectionRadius,
            rayDistance,
            raycastMask,
            QueryTriggerInteraction.Ignore
        );

        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (RaycastHit hit in hits)
        {
            Transform hitTransform = hit.collider.transform;

            if (hitTransform.IsChildOf(transform))
            {
                continue;
            }

            return FindTaggedParent(hitTransform);
        }

        return null;
    }

    Transform FindTaggedParent(Transform t)
    {
        while (t != null)
        {
            if (t.CompareTag(targetTag))
            {
                return t;
            }
            t = t.parent;
        }
        return null;
    }

    // 観測終了時の処理
    void EndObservation()
    {
        float endTime = Time.time;
        float duration = endTime - gazeStartTime;
        bool reached = duration >= requiredGazeTime;

        if (!reached)
        {
            Debug.Log(
                "観測終了(記録条件未達): " + currentTargetName +
                " / " + duration.ToString("F2") + "秒"
            );
        }
        else
        {
            Debug.Log(
                "観測終了: " + currentTargetName +
                " / " + duration.ToString("F2") + "秒"
            );
        }

        // CSVに1行追記(未達も Reached=False で残る)
        WriteObservation(endTime, duration, reached);
    }

    // 条件達成時のログ出力(Console)
    void RecordObservation(float duration)
    {
        Debug.Log(
            "========== Observation Log ==========\n" +
            "Object: " + currentTargetName + "\n" +
            "Tag: " + currentTargetTag + "\n" +
            "Position: " + currentTargetPosition + "\n" +
            "Start Time: " + gazeStartTime.ToString("F2") + "\n" +
            "Observation Time: " + duration.ToString("F2") + " seconds\n" +
            "======================================"
        );
    }
}