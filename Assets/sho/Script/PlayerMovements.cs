using System.Numerics;
using NUnit.Framework.Constraints;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMovements : MonoBehaviour
{
  [Header("移動スピード設定")]
  public float walkSpeed = 5.0f;
  public float fastSpeed = 8.0f;
  public float crouchSpeed = 3.5f;

  [Header("重力設定")]
  public float gravity = -9.81f;
  private float verticalVelocity = 0f;

  [Header("姿勢(頭の高さ)設定")]
  public float standingHeight = 1.7f;
  public float crouchingHeight = 0.9f;
  public float postureChangeSpeed = 0.8f;

  [Header("マウス視点感度")]
  public float mouseSensitivity = 0.15f;

  [HideInInspector] public bool isCrouching = false;
  [HideInInspector] public bool isFastMoving = false;

  private float cameraPitch = 0f;
  private CharacterController controller;

  void Start()
  {
    // Character Controllerコンポーネントを取得
    controller = GetComponent<CharacterController>();
    if (controller == null) // 修正：== に直しました
    {
      // もしアタッチされていなければ自動で追加
      controller = gameObject.AddComponent<CharacterController>();
    }

    Cursor.lockState = CursorLockMode.Locked;
    Cursor.visible = false;
  }

  void Update()
  {
    if (Keyboard.current == null) return;

    // ESCキーを押したらマウスカーソルを一時的に解除
    if (Keyboard.current.escapeKey.wasPressedThisFrame)
    {
      Cursor.lockState = CursorLockMode.None;
      Cursor.visible = true;
    }

    // ゲーム画面をもう一度クリックしたら再びロックする
    else if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
    {
      Cursor.lockState = CursorLockMode.Locked;
      Cursor.visible = false;
    }

    // しゃがみ、速足の状態判定
    isCrouching = Keyboard.current.cKey.isPressed || Keyboard.current.leftCtrlKey.isPressed;
    isFastMoving = !isCrouching && Keyboard.current.leftShiftKey.isPressed;

    float currentSpeed = walkSpeed;
    if (isCrouching) currentSpeed = crouchSpeed;
    else if (isFastMoving) currentSpeed = fastSpeed;

    // 左右・前後の入力判定
    float moveX = 0f;
    float moveZ = 0f;
    if (Keyboard.current != null)
    {
      if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) moveX = -1f;
      if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) moveX = 1f;
      if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) moveZ = -1f;
      if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) moveZ = 1f;
    }

    UnityEngine.Vector3 forward = transform.forward;
    UnityEngine.Vector3 right = transform.right;
    forward.y = 0f;
    right.y = 0f;
    UnityEngine.Vector3 moveDir = (forward.normalized * moveZ + right.normalized * moveX).normalized;

    // 重力の処理
    if (controller.isGrounded)
    {
      verticalVelocity = -0.5f;
    }
    else
    {
      verticalVelocity += gravity * Time.deltaTime;
    }

    // Character Controllerを使った移動（壁をすり抜けなくなる）
    UnityEngine.Vector3 velocity = moveDir * currentSpeed + UnityEngine.Vector3.up * verticalVelocity;
    controller.Move(velocity * Time.deltaTime);

    // しゃがみに合わせた高さ調整
    float targetHeight = isCrouching ? crouchingHeight : standingHeight;
    controller.height = Mathf.Lerp(controller.height, targetHeight, Time.deltaTime * postureChangeSpeed);

    // マウス視点移動
    if (Mouse.current != null && Cursor.lockState == CursorLockMode.Locked)
    {
      UnityEngine.Vector2 mouseDelta = Mouse.current.delta.ReadValue();

      // 左右の首の振り
      transform.Rotate(0f, mouseDelta.x * mouseSensitivity, 0f, Space.World);

      // 上下の首の振り
      cameraPitch -= mouseDelta.y * mouseSensitivity;
      cameraPitch = Mathf.Clamp(cameraPitch, -80f, 80f);
      transform.localEulerAngles = new UnityEngine.Vector3(cameraPitch, transform.localEulerAngles.y, 0f);
    }
  }
}