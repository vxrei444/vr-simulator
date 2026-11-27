using UnityEngine;

/// <summary>
/// 付けたオブジェクト（文字など）を、常にカメラの方へ向け、ふわふわ上下に動かす。
/// </summary>
public class Sho_FacingCharactor : MonoBehaviour
{
    [Tooltip("上下に動く幅")]
    public float bobHeight = 0.3f;
    [Tooltip("上下に動く速さ（1秒あたりの往復回数）")]
    public float bobSpeed = 0.5f;

    Vector3 startPos;

    void Start() => startPos = transform.position;

    void LateUpdate()
    {
        // ふわふわ上下
        transform.position = startPos + Vector3.up * Mathf.Sin(Time.time * bobSpeed * Mathf.PI * 2f) * bobHeight;

        // 常にカメラの方を向く（上下には傾けない）
        var cam = Camera.main;
        if (cam == null) return;
        Vector3 dir = transform.position - cam.transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.0001f) transform.rotation = Quaternion.LookRotation(dir);
    }
}