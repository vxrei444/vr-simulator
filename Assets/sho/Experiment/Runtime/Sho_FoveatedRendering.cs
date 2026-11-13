using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// 固定中心窩レンダリング（FFR）を有効にする。視野の端ほど粗い解像度で描くので、
/// 画面の端の霧・煙の計算（ピクセル数）が減る。目は視野の中心しか細かく見ていないので見た目の差は小さい。
///   ・Unity 標準の XR 表示機能（XRDisplaySubsystem.foveatedRenderingLevel, 0〜1）で設定
///   ・Meta XR SDK（OVRManager）があれば、そちらのレベルも合わせて設定（リフレクション）
/// Quest 実機（Vulkan）で効く。エディタ・PC では効果なし（エラーにもならない）。
/// </summary>
public class Sho_FoveatedRendering : MonoBehaviour
{
    public enum Level { Off = 0, Low = 1, Medium = 2, High = 3, HighTop = 4 }

    [Tooltip("強さ。High 推奨（端の解像度が大きく下がる。HighTop は上側をさらに粗くする）")]
    public Level level = Level.High;
    [Tooltip("重さに応じて Meta 側で強さを自動調整する（OVRManager がある場合）")]
    public bool dynamic = true;

    /// <summary>実際に設定できたか（ログ用）</summary>
    public bool Applied { get; private set; }
    /// <summary>描くピクセルの割合の目安（計算量の式で使う。Off=1.0）</summary>
    public float PixelFraction => level switch { Level.Low => 0.85f, Level.Medium => 0.75f, Level.High => 0.65f, Level.HighTop => 0.6f, _ => 1f };

    readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();
    float retry;

    void Start() => Apply();

    void Update()
    {
        // XR の起動が遅れることがあるので、設定できるまで 1 秒ごとに再試行
        if (Applied) return;
        retry += Time.unscaledDeltaTime;
        if (retry > 1f) { retry = 0f; Apply(); }
    }

    public void Apply()
    {
        float l = (int)level / 4f;
        SubsystemManager.GetSubsystems(displays);
        foreach (var d in displays)
        {
            if (!d.running) continue;
            d.foveatedRenderingFlags = XRDisplaySubsystem.FoveatedRenderingFlags.None; // 視線ではなく固定
            d.foveatedRenderingLevel = l;
            Applied = true;
        }

        // Meta XR SDK（OVRManager）がプロジェクトにあれば、そちらも設定
        var ovr = Type.GetType("OVRManager, Oculus.VR");
        if (ovr != null)
        {
            try
            {
                var lvProp = ovr.GetProperty("foveatedRenderingLevel", BindingFlags.Public | BindingFlags.Static);
                if (lvProp != null) lvProp.SetValue(null, Enum.ToObject(lvProp.PropertyType, (int)level));
                var dynProp = ovr.GetProperty("useDynamicFoveatedRendering", BindingFlags.Public | BindingFlags.Static);
                if (dynProp != null) dynProp.SetValue(null, dynamic);
                Applied = true;
            }
            catch (Exception e) { Debug.LogWarning("[Foveation] OVRManager の設定に失敗: " + e.Message); }
        }
        if (Applied) Debug.Log($"[Foveation] 固定中心窩レンダリング: {level}");
    }
}
