using UnityEngine;
using System.Collections;

public class FireAudioManager : MonoBehaviour
{
    public AudioSource alarmSource;     // 火災報知器のAudioSource
    public AudioSource announceSource;  // アナウンスのAudioSource

    [Header("時間の調整（秒）")]
    public float bellDuration = 3.0f;       // ベルを鳴らす時間（3秒）
    public float intervalSeconds = 1.0f;    // アナウンス終了後から次のベルまでの間（1秒）

    void Start()
    {
        StartCoroutine(LoopFireBroadcast());
    }

    IEnumerator LoopFireBroadcast()
    {
        // ゲームが動いている間、ずっとこのセットを繰り返す
        while (true)
        {
            // 1. 火災報知器（ベル）を鳴らし始める
            alarmSource.Play();

            // 2. 指定した秒数（3秒）だけベルを鳴らし続ける
            yield return new WaitForSeconds(bellDuration);

            // 3. 3秒経ったらベルを止める
            alarmSource.Stop();

            // 4. アナウンスを再生する
            announceSource.Play();

            // 5. アナウンスの音声が最後まで鳴り終わるまで待つ
            yield return new WaitWhile(() => announceSource.isPlaying);

            // 6. 次のサイクルに戻る前に少しだけ無音の間を入れる
            yield return new WaitForSeconds(intervalSeconds);
        }
    }
}