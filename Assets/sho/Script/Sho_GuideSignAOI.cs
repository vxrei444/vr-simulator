using UnityEngine;

/// <summary>
/// 誘導サイン（矢印・出口看板・点滅灯など）に付ける「注視対象(AOI)」の目印。
/// 視線レイがこのオブジェクトの Collider に当たると、ログに signId / designType が残る。
/// Collider が無い看板には BoxCollider(isTrigger) を付けること（体の当たり判定に干渉しない）。
/// </summary>
public class Sho_GuideSignAOI : MonoBehaviour
{
    [Tooltip("看板ごとに一意なID（例: S01_2F_corridor）")]
    public string signId = "S01";
    [Tooltip("デザイン種別（例: floor_arrow / green_runner / flashing / dynamic_arrow）")]
    public string designType = "floor_arrow";

    void Reset()
    {
        signId = gameObject.name;
        if (GetComponent<Collider>() == null)
        {
            var c = gameObject.AddComponent<BoxCollider>();
            c.isTrigger = true;
        }
    }
}