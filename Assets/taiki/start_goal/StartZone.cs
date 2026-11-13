using UnityEngine;

/// <summary>
/// スタート地点。プレイヤーがこのエリアから「出た瞬間」に計測開始（デフォルト）。
/// VRに慣れる・周りを見回す時間を脱出時間に含めないため。
/// </summary>
[RequireComponent(typeof(Collider))]
public class StartZone : MonoBehaviour
{
    public enum StartMode
    {
        OnExitZone,   // エリアから出たら開始（推奨）
        OnEnterZone,  // エリアに入ったら開始
        OnSceneLoad   // シーン開始と同時に開始
    }

    public StartMode mode = StartMode.OnExitZone;
    public string playerTag = "Player";

    void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
    }

    void Start()
    {
        if (mode == StartMode.OnSceneLoad)
            EvacuationSessionManager.Instance?.StartSession();
    }

    void OnTriggerEnter(Collider other)
    {
        if (mode == StartMode.OnEnterZone && other.CompareTag(playerTag))
            EvacuationSessionManager.Instance?.StartSession();
    }

    void OnTriggerExit(Collider other)
    {
        if (mode == StartMode.OnExitZone && other.CompareTag(playerTag))
            EvacuationSessionManager.Instance?.StartSession();
    }

    void OnDrawGizmos()
    {
        var col = GetComponent<Collider>();
        if (col == null) return;
        Gizmos.color = new Color(0f, 1f, 0f, 0.25f);
        Gizmos.DrawCube(col.bounds.center, col.bounds.size);
        Gizmos.color = Color.green;
        Gizmos.DrawWireCube(col.bounds.center, col.bounds.size);
    }
}
