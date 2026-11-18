// 煙層を「立体の霧」として描くシェーダー（URP / Quest 対応・簡易ボリューム）。
// 煙層を囲む箱の中を、視線に沿って数か所（_Steps）調べ、煙の濃さ（減光係数 K）を足し合わせて
// 透け具合を計算する。境目の凹凸と濃さのむらは 3D ノイズ（テクスチャ）で作るので軽い。
//   ・カメラが箱の中にいても外にいても描ける（裏面を描き、箱との交点を自分で計算）
//   ・壁や床より奥は描かない（カメラの深度テクスチャを使う → カメラの Depth Texture をオンにすること）
//   ・火元からの広がり（_Origin, _FrontM, _TaperM）は SmokeLayerController と同じ式
Shader "Sho/SmokeVolume"
{
    Properties
    {
        _BaseColor ("煙の色", Color) = (0.16, 0.15, 0.14, 1)
        _NoiseTex ("3Dノイズ", 3D) = "white" {}
        _NoiseScale ("模様の大きさ [1/m]", Float) = 0.22
        _NoiseSpeed ("流れる速さ [m/s]", Vector) = (0.05, 0.012, 0.03, 0)
        _WaveAmpM ("境目の凹凸 [m]", Float) = 0.35
        _BlendM ("境目のぼかし [m]", Float) = 0.3
        _DensityVar ("濃さのむら", Range(0, 1)) = 0.5
        _VisualScale ("見た目の濃さの倍率", Range(0.1, 3)) = 1
        _Steps ("サンプル数（多いほどきれい・重い）", Range(4, 24)) = 10
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+10" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "SmokeVolume"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Front

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE3D(_NoiseTex);
            SAMPLER(sampler_NoiseTex);

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float _NoiseScale;
                float4 _NoiseSpeed;
                float _WaveAmpM;
                float _BlendM;
                float _DensityVar;
                float _VisualScale;
                float _Steps;
                // 以下はスクリプト（Sho_SmokeVolumeVisual）が毎フレーム設定
                float4 _BoxMin;
                float4 _BoxMax;
                float _LayerY;
                float _CeilY;
                float _K;
                float _Upm;
                float4 _Origin;
                float _FrontM;
                float _TaperM;
                float _Seed;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionHCS = p.positionCS;
                o.positionWS = p.positionWS;
                return o;
            }

            float2 RayBox(float3 ro, float3 rd, float3 bmin, float3 bmax)
            {
                float3 inv = 1.0 / rd;
                float3 t0 = (bmin - ro) * inv;
                float3 t1 = (bmax - ro) * inv;
                float3 tmin = min(t0, t1);
                float3 tmax = max(t0, t1);
                return float2(max(max(tmin.x, tmin.y), tmin.z), min(min(tmax.x, tmax.y), tmax.z));
            }

            float Ign(float2 px) // ノイズ状のずらし（縞模様を消す）
            {
                return frac(52.9829189 * frac(dot(px, float2(0.06711056, 0.00583715))));
            }

            // 位置 p（ワールド）の減光係数 [1/m]
            float Density(float3 p)
            {
                float upm = max(_Upm, 0.01);
                float3 pm = p / upm;
                float3 q = pm * _NoiseScale + _NoiseSpeed.xyz * _Time.y + _Seed;
                float n1 = SAMPLE_TEXTURE3D_LOD(_NoiseTex, sampler_NoiseTex, q, 0).r;
                float n2 = SAMPLE_TEXTURE3D_LOD(_NoiseTex, sampler_NoiseTex, q * 2.3 + 0.37, 0).r;

                float dist = length(pm.xz - _Origin.xz / upm);
                float front = saturate((_FrontM - dist) / max(_FrontM * 0.15, 0.5));
                float layer = _LayerY + _TaperM * upm * saturate(dist / max(_FrontM, 0.01));
                layer += (n1 - 0.5) * 2.0 * _WaveAmpM * upm;           // 境目の凹凸
                layer = min(layer, _CeilY);
                float h = smoothstep(layer - _BlendM * upm, layer + _BlendM * upm, p.y);
                float dv = lerp(1.0, 0.4 + 1.2 * n2, _DensityVar);      // 濃さのむら
                return _K * h * dv * front * _VisualScale;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float3 ro = GetCameraPositionWS();
                float3 rd = normalize(i.positionWS - ro);
                rd += (float3)(abs(rd) < 1e-5) * 1e-5;

                float2 tb = RayBox(ro, rd, _BoxMin.xyz, _BoxMax.xyz);
                float tNear = max(tb.x, 0.0);
                float tFar = tb.y;

                // 壁・床より奥は描かない
                float2 uv = GetNormalizedScreenSpaceUV(i.positionHCS);
                float eyeD = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
                float3 camFwd = -UNITY_MATRIX_V[2].xyz;
                tFar = min(tFar, eyeD / max(dot(rd, camFwd), 1e-4));
                if (tFar <= tNear) return half4(0, 0, 0, 0);

                int steps = (int)clamp(_Steps, 4, 24);
                float dt = (tFar - tNear) / steps;
                float dtM = dt / max(_Upm, 0.01);
                float jitter = Ign(i.positionHCS.xy);
                float T = 1.0;
                [loop]
                for (int s = 0; s < 24; s++)
                {
                    if (s >= steps) break;
                    float3 p = ro + rd * (tNear + (s + jitter) * dt);
                    T *= exp(-Density(p) * dtM);
                    if (T < 0.01) break;
                }
                return half4(_BaseColor.rgb, saturate(1.0 - T));
            }
            ENDHLSL
        }
    }
    FallBack Off
}
