using System;
using UnityEngine;

public class PlayerDirectionLogger : MonoBehaviour
{
    [Header("観測判定設定")]
    [SerializeField] private string targetTag = "GazeTarget";

    // 連続して観測する必要がある時間(秒)
    [SerializeField] private float requiredGazeTime = 3.0f;

    [Header("視界・Ray設定")]
    [SerializeField] private float rayDistance = 10.0f;

    // 視線の太さ(球の半径)
    [SerializeField] private float detectionRadius = 0.15f;

    // 視線を遮る対象にするレイヤー(通常は Everything)
    [SerializeField] private LayerMask raycastMask = ~0;

    private Transform currentTarget;
    private float gazeStartTime;
    private bool hasLogged = false;

    void Update()
    {
        CheckGazeTarget();
    }

    void OnDisable()
    {
        // 観測中に停止した場合も終了ログを出す
        if (currentTarget != null)
        {
            EndObservation();
            currentTarget = null;
        }
    }

    // 正面方向に対象物があるか確認
    void CheckGazeTarget()
    {
        Transform hitTarget = FindGazeTarget();

        // 観測対象が切り替わった場合(視界から外れた場合を含む)
        if (hitTarget != currentTarget)
        {
            if (currentTarget != null)
            {
                EndObservation();
            }

            currentTarget = hitTarget;

            if (currentTarget != null)
            {
                gazeStartTime = Time.time;
                hasLogged = false;
                Debug.Log("観測開始: " + currentTarget.name);
            }
        }

        if (currentTarget == null)
        {
            return;
        }

        // 連続観測時間を計算
        float gazeDuration = Time.time - gazeStartTime;

        if (gazeDuration >= requiredGazeTime && !hasLogged)
        {
            RecordObservation(gazeDuration);
            hasLogged = true;
        }
    }

    // 視線上で最初に当たったものが対象タグなら、それを返す
    Transform FindGazeTarget()
    {
        Ray ray = new Ray(transform.position, transform.forward);

        RaycastHit[] hits = Physics.SphereCastAll(
            ray,
            detectionRadius,
            rayDistance,
            raycastMask,
            QueryTriggerInteraction.Ignore
        );

        // 近い順に並べる
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (RaycastHit hit in hits)
        {
            Transform hitTransform = hit.collider.transform;

            // 自分自身(プレイヤー)の階層は無視する
            if (hitTransform.IsChildOf(transform))
            {
                continue;
            }

            // 最初に当たったものだけで判定する(遮蔽を考慮)
            return FindTaggedParent(hitTransform);
        }

        return null;
    }

    // 自分または親をたどって、対象タグを持つTransformを探す
    Transform FindTaggedParent(Transform t)
    {
        while (t != null)
        {
            if (t.CompareTag(targetTag))
            {
                return t;
            }
            t = t.parent;
        }
        return null;
    }

    // 観測終了時の処理
    void EndObservation()
    {
        // 対象が破棄されている場合の安全対策
        string targetName = currentTarget != null ? currentTarget.name : "(破棄済み)";
        float duration = Time.time - gazeStartTime;

        if (duration < requiredGazeTime)
        {
            Debug.Log(
                "観測終了(記録条件未達): " + targetName +
                " / " + duration.ToString("F2") + "秒"
            );
        }
        else
        {
            Debug.Log(
                "観測終了: " + targetName +
                " / " + duration.ToString("F2") + "秒"
            );
        }
    }

    // 条件達成時のログ出力
    void RecordObservation(float duration)
    {
        Vector3 objectPosition = currentTarget.position;

        Debug.Log(
            "========== Observation Log ==========\n" +
            "Object: " + currentTarget.name + "\n" +
            "Tag: " + currentTarget.tag + "\n" +
            "Position: " + objectPosition + "\n" +
            "Start Time: " + gazeStartTime.ToString("F2") + "\n" +
            "Observation Time: " + duration.ToString("F2") + " seconds\n" +
            "======================================"
        );
    }
}