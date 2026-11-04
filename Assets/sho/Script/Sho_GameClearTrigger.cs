using UnityEngine;
using UnityEngine.SceneManagement; //これはシーン管理に必要

[RequireComponent(typeof(Collider))]
public class Sho_GameClearTrigger :MonoBehaviour
{ 
    public string goalName  = "Exit_1";
    public string playerTag = "Player";

    [SerializeField] private string resultSceneName = "ResultScene";

    void Reset()
    {
      var col = GetComponent<Collider>();
      if(col != null) col.isTrigger = true;
      goalName = gameObject.name;
    }

    void OnTriggerEnter(Collider other)
    {
      if(other.CompareTag(playerTag))
      {
        //計測とログ処理
        EvacuationSessionManager.Instance?.ReachGoal(goalName);

        //リザルト画面に切り替える処理
        SceneManager.LoadScene(resultSceneName);
      }
    }
    
    
    void OnDrawGizmos()
    {
      var col = GetComponent<Collider>();
      if(col == null) return;
      Gizmos.color = new Color(1f,0f,0f,0.25f);
      Gizmos.DrawCube(col.bounds.center, col.bounds.size);
      Gizmos.color = Color.red;
      Gizmos.DrawWireCube(col.bounds.center, col.bounds.size);
    }
}
