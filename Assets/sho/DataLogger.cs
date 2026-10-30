using UnityEngine;
using System.IO;
using System.Text;
using Unity.VisualScripting;

//3D空間のプレイヤーの位置や向きならVector3
//二次元空間(UI)の座標や画面上のマウスの位置などを扱うときにはVector2

public class DataLogger : MonoBehaviour
{
    [Header("実験メタデータ")]  
    public string participantId = "User01";
    public string conditionName = "Baseline";
    
    [Header("記録設定")]
    [Tooltip("何秒ごとにログを記録するか)")]
    public float logInterval = 0.1f;

    [Tooltip("視線方向を取得するためのカメラ(指定しなければMainCamera)")]
    public Transform targetCamera;

    [Tooltip("視線が届く最大距離")]
    public float gazeRayDistance = 10.0f;

    private float timer = 0f;
    private float elapsedTime = 0f;
    private StringBuilder csvContent;
    private string filePath;
    
    void Start()
    {
        //カメラが指定されていなければメインカメラを使う
        if(targetCamera == null && Camera.main != null)
        {
            targetCamera = Camera.main.transform;
        }
    

        //保存したいファイルの準備
        string dirPath = Path.Combine(Application.dataPath, "../Logs");
        if (!Directory.Exists(dirPath))
            {
                Directory.CreateDirectory(dirPath);
            }
        // ファイル名の生成（ID、条件、日時に基づく）
        string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        filePath = Path.Combine(dirPath, $"Log_{participantId}_{conditionName}_{timestamp}.csv");

        // CSVのヘッダー（項目名）を作成
        csvContent = new StringBuilder();
        csvContent.AppendLine("ElapsedTime,PosX,PosY,PosZ,GazeX,GazeY,GazeZ,TargetObject,ActionState");

        Debug.Log($"ログ記録開始: {filePath}");
    }


        // Update is called once per frame
    void Update()
    {
        //経過時間
        elapsedTime += Time.deltaTime;
        timer += Time.deltaTime;

        //一定間隔ごとにデータを一行ごとに取得する
        if(timer >= logInterval)
        {
            timer = 0f;
            RecordData();
        }

        //テスト用：Lキーを押したら酒豪で即座にCSVを保存できる
        if(UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.lKey.wasPressedThisFrame)
        {
            SaveLogToFile();
            Debug.Log("ログを保存しました。(手動)");
        }
    }

    void RecordData()
    {
        //位置座標の取得
        UnityEngine.Vector3 pos = transform.position;

        //視線方向の取得
        UnityEngine.Vector3 gaze = (targetCamera 
        != null) ? targetCamera.forward : UnityEngine.Vector3.forward;

        //視線の先にあるオブジェクトの検出(見たかどうか)
        string targetObjectName = "None";
        if(targetCamera != null)
        {
            Ray ray = new Ray(targetCamera.position, targetCamera.forward);
            RaycastHit hit;
            if(Physics.Raycast(ray, out hit, gazeRayDistance))
            {
                //ぶつかったオブジェクトの名前を取得
                targetObjectName = hit.collider.gameObject.name;
            }
        }

        //行動状態の判定(PlayerMovementsスクリプトから状態を取得)
        string actionState = "Normal";
        PlayerMovements pm = GetComponent<PlayerMovements>();
        if(participantId != null)
        {
            if(pm.isCrouching)actionState = "Crouching"; //しゃがみ
            else if(pm.isFastMoving) actionState = "FastMoving";//早歩き
            else actionState = "Walking"; //通常
        }

        string line = $"{elapsedTime:F2},{pos.x:F2},{pos.y:F2},{pos.z:F2},{gaze.x:F2},{gaze.y:F2},{gaze.z:F2},{targetObjectName},{actionState}";
        csvContent.AppendLine(line);
    }

    void OnApplicationQuit()
    {
        SaveLogToFile();
    }

    private void SaveLogToFile()
    {
        if (csvContent != null)
        {
            File.WriteAllText(filePath, csvContent.ToString(), Encoding.UTF8);
            Debug.Log($"ログをファイルに保存しました: {filePath}");
        }
    }

}
