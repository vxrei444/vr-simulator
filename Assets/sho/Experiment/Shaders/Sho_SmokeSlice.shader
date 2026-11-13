// 煙層の「1枚の板」用シェーダー（URP / Quest 対応）。
// ワールド座標の3Dノイズをゆっくり動かして、モコモコした煙のまだら模様を作る。
// 板を何枚か重ねると、境目に厚みと奥行きが出る（Sho_SmokeLayerVisual が並べる）。
// テクスチャ不要。カメラのすぐ近くは自動で薄くして、板の「切り口」が目立たないようにしている。
Shader "Sho/SmokeSlice"
{
    Properties
    {
        _BaseColor ("煙の色", Color) = (0.16, 0.15, 0.14, 1)
        _Alpha ("濃さ", Range(0, 1)) = 0.5
        _Coverage ("覆う割合（大きいほど隙間が減る）", Range(0, 1)) = 0.6
        _Softness ("まだらの縁のぼかし", Range(0.01, 1)) = 0.3
        _NoiseScale ("模様の細かさ [1/m]", Float) = 0.45
        _NoiseSpeed ("模様が流れる速さ [m/s]", Vector) = (0.06, 0.02, 0.04, 0)
        _NearFade ("カメラ近くで薄くする距離 [m]", Float) = 1.0
        _UnitsPerMeter ("1m あたりのユニット数", Float) = 1
        _Seed ("模様のずらし", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "SmokeSlice"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half _Alpha;
                half _Coverage;
                half _Softness;
                float _NoiseScale;
                float4 _NoiseSpeed;
                float _NearFade;
                float _UnitsPerMeter;
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
                float fogFactor : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float Hash(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float ValueNoise(float3 x)
            {
                float3 i = floor(x);
                float3 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                float a = lerp(Hash(i + float3(0, 0, 0)), Hash(i + float3(1, 0, 0)), f.x);
                float b = lerp(Hash(i + float3(0, 1, 0)), Hash(i + float3(1, 1, 0)), f.x);
                float c = lerp(Hash(i + float3(0, 0, 1)), Hash(i + float3(1, 0, 1)), f.x);
                float d = lerp(Hash(i + float3(0, 1, 1)), Hash(i + float3(1, 1, 1)), f.x);
                return lerp(lerp(a, b, f.y), lerp(c, d, f.y), f.z);
            }

            float Fbm(float3 p)
            {
                float v = 0.5 * ValueNoise(p);
                p = p * 2.03 + 17.1;
                v += 0.3 * ValueNoise(p);
                p = p * 2.01 + 9.7;
                v += 0.2 * ValueNoise(p);
                return v;
            }

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                VertexPositionInputs pos = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionHCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.fogFactor = ComputeFogFactor(pos.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float upm = max(_UnitsPerMeter, 0.01);
                float3 pm = i.positionWS / upm;                       // 実寸 [m]
                float3 p = pm * _NoiseScale + _NoiseSpeed.xyz * _Time.y + _Seed;
                float n = Fbm(p);
                float edge = 1.0 - _Coverage;
                float a = smoothstep(edge - _Softness, edge + _Softness, n);

                float d = distance(GetCameraPositionWS(), i.positionWS) / upm;
                a *= saturate(d / max(_NearFade, 0.01));              // カメラ近くは薄く

                half4 c = half4(_BaseColor.rgb, saturate(a * _Alpha));
                c.rgb = MixFog(c.rgb, i.fogFactor);
                return c;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
