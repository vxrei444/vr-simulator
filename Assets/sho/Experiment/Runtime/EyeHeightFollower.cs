using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// PC 操作（Sho_PlayerMovements）でしゃがんだとき、カメラ（目の高さ）も下げる。
/// カメラに付ける。CharacterController の高さに合わせて目の位置を動かす。
/// VR ヘッドセット使用中は、実際の頭の高さが使われるので何もしない。
/// </summary>
public class EyeHeightFollower : MonoBehaviour
{
    public CharacterController controller;
    [Tooltip("頭のてっぺんから目までの距離(m)")]
    public float eyeBelowTop = 0.12f;

    void Start()
    {
        if (controller == null) controller = GetComponentInParent<CharacterController>();
    }

    void LateUpdate()
    {
        if (controller == null || XRSettings.isDeviceActive) return;
        var p = transform.localPosition;
        p.y = controller.center.y + controller.height * 0.5f - eyeBelowTop;
        transform.localPosition = p;
    }
}
