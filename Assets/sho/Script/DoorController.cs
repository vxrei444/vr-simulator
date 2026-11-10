using UnityEngine;

public class DoorController : MonoBehaviour
{
    [Header("開閉設定")]
    public float openAngle = 110f;      // 開く角度
    public float smooth = 5.0f;         // 開くスピード

    private bool isOpen = false;        // 現在開いているか
    private Quaternion closedRotation;  // 閉まっているときの角度
    private Quaternion openRotation;    // 開いているときの目標角度

    void Start()
    {
        // 1. 初期位置（閉じた状態）の回転を記憶
        closedRotation = transform.rotation;

        // 2. スタート時に「開いたときの角度」もあらかじめ計算しておく
        // ※ドアのローカルY軸を基準に回転させたい場合は Quaternion.AngleAxis を使うと確実だよ
        openRotation = closedRotation * Quaternion.Euler(0, -openAngle, 0);
    }

    void Update()
    {
        // 目標の角度へ滑らかに回転させる
        Quaternion targetRot = isOpen ? openRotation : closedRotation;
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * smooth);
    }

    // プレイヤー側から呼び出される開閉メソッド
    public void ToggleDoor(Transform playerTransform)
    {
        isOpen = !isOpen;

        // 応用：もし「プレイヤーがドアのどちら側にいるかによって開く向きを変えたい」場合は、
        // ここで playerTransform との位置関係を計算して openRotation を動的に再計算させるとさらにリッチになるよ！
    }
}