using UnityEngine;

public class BlinkSign : MonoBehaviour
{
    public float blinkSpeed = 3.0f;   // 点滅の速さ
    public float maxIntensity = 2.5f; // 一番明るい時の強さ
    public float minIntensity = 0.2f; // 一番暗い時の強さ

    private Material mat;

    void Start()
    {
        // この看板についているマテリアルを取得し、発光機能を有効にする
        mat = GetComponent<Renderer>().material;
        mat.EnableKeyword("_EMISSION");
    }

    void Update()
    {
        // 時間経過に合わせて 0.0 ～ 1.0 の間を往復する数値を作る
        float t = (Mathf.Sin(Time.time * blinkSpeed) + 1.0f) / 2.0f;
        
        // 明るさを計算してマテリアルのEmission Colorに反映させる
        float intensity = Mathf.Lerp(minIntensity, maxIntensity, t);
        mat.SetColor("_EmissionColor", Color.white * intensity);
    }
}