using UnityEngine;

/// <summary>
/// 廊下の分岐点・交差点に置くトリガー。
/// 入った時刻・出た時刻・滞在時間・どの方向に出て行ったかを events.csv に記録する。
/// exits に各方向の目印Transform（例: toExit / toDeadEnd）を置くと、
/// 退出時に一番近い方向の名前が記録される。
/// </summary>
[RequireComponent(typeof(Collider))]
public class DecisionPointZone : MonoBehaviour
{
    public string pointId = "DP01";
    public string playerTag = "Player";

    [Tooltip("分岐の各方向の目印（名前がそのままログに残る。例: toExit / toDeadEnd）")]
    public Transform[] exits;
    [Tooltip("正解（出口へ向かう）方向の名前")]
    public string correctExitName = "toExit";

    float enterTime;
    int visitCount;

    void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
        pointId = gameObject.name;
    }

    void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag(playerTag) || EvacuationTrajectoryLogger.Instance == null) return;
        visitCount++;
        enterTime = Time.time;
        EvacuationTrajectoryLogger.Instance.LogEvent("dp_enter", pointId, $"visit={visitCount}");
    }

    void OnTriggerExit(Collider other)
    {
        if (!other.CompareTag(playerTag) || EvacuationTrajectoryLogger.Instance == null) return;
        string dir = NearestExit(other.transform.position);
        string ok = dir == correctExitName ? "correct" : "wrong";
        float dwell = Time.time - enterTime;
        EvacuationTrajectoryLogger.Instance.LogEvent("dp_exit", pointId, $"dir={dir};{ok};dwell={dwell:F2};visit={visitCount}");
    }

    string NearestExit(Vector3 p)
    {
        if (exits == null || exits.Length == 0) return "unknown";
        Transform best = null; float bd = float.MaxValue;
        foreach (var e in exits)
        {
            if (e == null) continue;
            float d = (e.position - p).sqrMagnitude;
            if (d < bd) { bd = d; best = e; }
        }
        return best != null ? best.name : "unknown";
    }

    void OnDrawGizmos()
    {
        var col = GetComponent<Collider>();
        if (col == null) return;
        Gizmos.color = new Color(1f, 0.8f, 0f, 0.25f);
        Gizmos.DrawCube(col.bounds.center, col.bounds.size);
        if (exits == null) return;
        foreach (var e in exits)
        {
            if (e == null) continue;
            Gizmos.color = e.name == correctExitName ? Color.green : Color.red;
            Gizmos.DrawLine(col.bounds.center, e.position);
        }
    }
}