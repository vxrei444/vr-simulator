using UnityEngine;

/// <summary>
/// 【taiki】分析用の地図（taiki_LayoutExporter が出力する JSON）に載せたいオブジェクトに付ける目印。
/// 例：炎・瓦礫などの障害物・誘導標識・出口。
/// ゲーム中の動作には一切影響しない（エディタでの書き出し時に読まれるだけ）。
/// ※ FireVisual が付いたオブジェクトは、この目印が無くても自動で「炎」として書き出される。
/// </summary>
public class taiki_MapMarker : MonoBehaviour
{
    public enum taiki_Kind { Fire, Obstacle, Sign, Exit, Other }

    [Tooltip("図での表示の種類（Fire=炎、Obstacle=障害物、Sign=標識、Exit=出口、Other=その他）")]
    public taiki_Kind taiki_kind = taiki_Kind.Fire;

    [Tooltip("図に添える名前（空ならオブジェクト名）")]
    public string taiki_label = "";

    [Tooltip("大きさ(m)。0ならコライダー/レンダラーの大きさを自動で使う")]
    public Vector2 taiki_sizeXZ = Vector2.zero;

    private void OnDrawGizmos()
    {
        Color taiki_c = taiki_kind == taiki_Kind.Fire ? new Color(1f, 0.3f, 0f, 0.8f)
                      : taiki_kind == taiki_Kind.Obstacle ? new Color(0.4f, 0.4f, 0.4f, 0.8f)
                      : new Color(0.2f, 0.6f, 1f, 0.8f);
        Gizmos.color = taiki_c;
        Gizmos.DrawWireSphere(transform.position, 0.4f);
    }
}
