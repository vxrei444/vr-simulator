using UnityEngine;

/// <summary>
/// VRプレイヤーの「当たり判定用の体」。頭（CenterEyeAnchor）の真下に
/// カプセルを追従させ、StartZone / GoalZone のトリガーを確実に発火させる。
/// ・スティック移動でもルームスケール（実際に歩く）でも判定がずれない
/// ・このオブジェクトは OVRCameraRig の子にしない（独立したオブジェクトに付ける）
/// ・Tag は "Player" にする
/// </summary>
[RequireComponent(typeof(CapsuleCollider), typeof(Rigidbody))]
public class PlayerBodyCollider : MonoBehaviour
{
    [Tooltip("OVRCameraRig > TrackingSpace > CenterEyeAnchor")]
    public Transform head;

    [Tooltip("床の高さの基準。OVRCameraRig（Tracking Origin = Floor Level）を入れる")]
    public Transform floorReference;

    [Tooltip("体の半径(m)")]
    public float radius = 0.25f;

    Rigidbody rb;
    CapsuleCollider capsule;

    void Reset()
    {
        gameObject.tag = "Player";
    }

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.isKinematic = true;      // 物理で押されない・重力なし
        rb.useGravity = false;
        rb.interpolation = RigidbodyInterpolation.Interpolate;

        capsule = GetComponent<CapsuleCollider>();
        capsule.isTrigger = false;
        capsule.radius = radius;
        capsule.direction = 1;      // Y軸方向
        capsule.center = Vector3.zero;

        if (head == null && Camera.main != null)
        {
            head = Camera.main.transform;
            Debug.LogWarning("[PlayerBody] head 未設定のため Camera.main を使用します。");
        }
    }

    void FixedUpdate()
    {
        if (head == null) return;

        Vector3 headPos = head.position;
        float floorY = floorReference != null ? floorReference.position.y : 0f;

        // 足元(floorY)〜頭(headPos.y) を覆うカプセルにする
        float height = Mathf.Max(headPos.y - floorY, radius * 2f);
        capsule.height = height;

        Vector3 target = new Vector3(headPos.x, floorY + height * 0.5f, headPos.z);
        rb.MovePosition(target);
    }
}
