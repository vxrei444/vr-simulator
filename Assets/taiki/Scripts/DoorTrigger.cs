using UnityEngine;

/// <summary>
/// ドアの開口部に置く判定エリア。プレイヤーが通過すると DemoManager に通知する。
/// BoxCollider（Is Trigger = ON）と一緒に付けてください。
/// </summary>
[RequireComponent(typeof(Collider))]
public class DoorTrigger : MonoBehaviour
{
    public enum DoorType { Start, Goal }
    public DoorType doorType = DoorType.Start;

    void Reset()
    {
        // コンポーネント追加時に自動で Trigger にする
        GetComponent<Collider>().isTrigger = true;
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.GetComponentInParent<DemoPlayerController>() == null) return;

        if (DemoManager.Instance == null)
        {
            Debug.LogWarning("DemoManager がシーンにありません");
            return;
        }

        if (doorType == DoorType.Start) DemoManager.Instance.OnStartDoor();
        else DemoManager.Instance.OnGoalDoor();
    }

    // シーンビューで判定エリアが見えるようにする（スタート=緑、ゴール=赤）
    void OnDrawGizmos()
    {
        var box = GetComponent<BoxCollider>();
        if (box == null) return;
        Gizmos.matrix = transform.localToWorldMatrix;
        Color c = doorType == DoorType.Start ? new Color(0f, 1f, 0.3f) : new Color(1f, 0.25f, 0.2f);
        Gizmos.color = new Color(c.r, c.g, c.b, 0.25f);
        Gizmos.DrawCube(box.center, box.size);
        Gizmos.color = c;
        Gizmos.DrawWireCube(box.center, box.size);
    }
}
