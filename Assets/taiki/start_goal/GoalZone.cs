using UnityEngine;

/// <summary>
/// ゴール（出口）。プレイヤーが入った瞬間に計測終了。
/// 出口が複数ある場合はそれぞれに付け、goalName を変えておくと
/// 「どの出口から逃げたか」がログに残る。
/// </summary>
[RequireComponent(typeof(Collider))]
public class GoalZone : MonoBehaviour
{
    public string goalName = "Exit_1";
    public string playerTag = "Player";

    void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
        goalName = gameObject.name;
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag(playerTag))
            EvacuationSessionManager.Instance?.ReachGoal(goalName);
    }

    void OnDrawGizmos()
    {
        var col = GetComponent<Collider>();
        if (col == null) return;
        Gizmos.color = new Color(1f, 0f, 0f, 0.25f);
        Gizmos.DrawCube(col.bounds.center, col.bounds.size);
        Gizmos.color = Color.red;
        Gizmos.DrawWireCube(col.bounds.center, col.bounds.size);
    }
}
