using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// デモ用のキーボード＋マウス操作（一人称視点）。
/// WASD / ↑↓ : 移動　　←→ / Q E : 向きを変える　　マウス : 視点
/// Shift : 走る　　Esc : カーソル解除　　クリック : カーソル再ロック
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class DemoPlayerController : MonoBehaviour
{
    [Header("移動")]
    public float walkSpeed = 2.0f;
    public float runSpeed = 4.0f;
    public float gravity = -9.81f;

    [Header("視点")]
    [Tooltip("子オブジェクトのカメラ。空なら自動で探します")]
    public Transform cameraTransform;
    public float mouseSensitivity = 0.15f;   // 度 / ピクセル
    public float keyTurnSpeed = 90f;         // 度 / 秒（←→, Q/E）
    public float maxPitch = 80f;

    [HideInInspector] public bool canMove = true;

    CharacterController cc;
    float pitch;
    float verticalVelocity;

    void Awake()
    {
        cc = GetComponent<CharacterController>();
        if (cameraTransform == null)
        {
            var cam = GetComponentInChildren<Camera>();
            if (cam != null) cameraTransform = cam.transform;
        }
    }

    void Start() => LockCursor(true);

    public void LockCursor(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    public void ResetTo(Vector3 position, Quaternion rotation)
    {
        cc.enabled = false;
        transform.SetPositionAndRotation(position, rotation);
        cc.enabled = true;
        pitch = 0f;
        verticalVelocity = 0f;
        if (cameraTransform != null) cameraTransform.localEulerAngles = Vector3.zero;
    }

    void Update()
    {
        if (!canMove) return;

        ReadInput(out Vector2 move, out Vector2 look, out float turn, out bool run,
                  out bool escPressed, out bool clickPressed);

        if (escPressed) LockCursor(false);
        if (clickPressed && Cursor.lockState != CursorLockMode.Locked) LockCursor(true);
        if (Cursor.lockState != CursorLockMode.Locked) look = Vector2.zero;

        // 向き（左右はプレイヤー本体、上下はカメラ）
        transform.Rotate(0f, look.x * mouseSensitivity + turn * keyTurnSpeed * Time.deltaTime, 0f);
        pitch = Mathf.Clamp(pitch - look.y * mouseSensitivity, -maxPitch, maxPitch);
        if (cameraTransform != null) cameraTransform.localEulerAngles = new Vector3(pitch, 0f, 0f);

        // 移動
        Vector3 dir = transform.right * move.x + transform.forward * move.y;
        if (dir.sqrMagnitude > 1f) dir.Normalize();
        float speed = run ? runSpeed : walkSpeed;

        if (cc.isGrounded && verticalVelocity < 0f) verticalVelocity = -1f;
        verticalVelocity += gravity * Time.deltaTime;

        cc.Move((dir * speed + Vector3.up * verticalVelocity) * Time.deltaTime);
    }

    void ReadInput(out Vector2 move, out Vector2 look, out float turn, out bool run,
                   out bool escPressed, out bool clickPressed)
    {
        move = Vector2.zero; look = Vector2.zero; turn = 0f; run = false;
        escPressed = false; clickPressed = false;

#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current;
        var m = Mouse.current;
        if (k != null)
        {
            if (k.wKey.isPressed || k.upArrowKey.isPressed) move.y += 1f;
            if (k.sKey.isPressed || k.downArrowKey.isPressed) move.y -= 1f;
            if (k.dKey.isPressed) move.x += 1f;
            if (k.aKey.isPressed) move.x -= 1f;
            if (k.rightArrowKey.isPressed || k.eKey.isPressed) turn += 1f;
            if (k.leftArrowKey.isPressed || k.qKey.isPressed) turn -= 1f;
            run = k.leftShiftKey.isPressed || k.rightShiftKey.isPressed;
            escPressed = k.escapeKey.wasPressedThisFrame;
        }
        if (m != null)
        {
            look = m.delta.ReadValue();
            clickPressed = m.leftButton.wasPressedThisFrame;
        }
#else
        if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) move.y += 1f;
        if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) move.y -= 1f;
        if (Input.GetKey(KeyCode.D)) move.x += 1f;
        if (Input.GetKey(KeyCode.A)) move.x -= 1f;
        if (Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.E)) turn += 1f;
        if (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.Q)) turn -= 1f;
        run = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        // 旧Input Managerのマウス軸はおよそ「ピクセル×0.1」なので10倍して揃える
        look = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 10f;
        escPressed = Input.GetKeyDown(KeyCode.Escape);
        clickPressed = Input.GetMouseButtonDown(0);
#endif
    }
}
