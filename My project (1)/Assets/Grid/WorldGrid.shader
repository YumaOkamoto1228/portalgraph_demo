// ワールド座標基準のグリッド。オブジェクトのScaleを変えてもマス目の大きさは変わらない
// 壁の向き（法線）に合わせて、床なら XZ、奥の壁なら XY、横の壁なら ZY 平面にマス目を描く
// ※ 壁は X/Y/Z 軸に沿った向き（0°/90°/180°/270°回転）で置くこと
Shader "Custom/WorldGrid"
{
    Properties
    {
        _CellSize ("マス目の大きさ (Unity単位)", Float) = 0.25
        _LineWidth ("線の太さ (Unity単位)", Float) = 0.008
        [HDR] _LineColor ("線の色 (HDR・Bloomで光る)", Color) = (2.0, 0.6, 1.6, 1)
        _BaseColor ("下地の色", Color) = (0.10, 0.02, 0.07, 1)
        _GlowWidth ("線のにじみ幅 (Unity単位)", Float) = 0.02
        _GlowIntensity ("線のにじみの強さ", Range(0, 2)) = 0.35
        _ShadowStrength ("落ち影の濃さ", Range(0, 1)) = 0.7
        _GridOffset ("マス目の基準位置 (ワールド座標)", Vector) = (0, 0, 0, 0)
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("描画する面 (Off=両面)", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Cull [_Cull]

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float _CellSize;
            float _LineWidth;
            float4 _LineColor;
            float4 _BaseColor;
            float _GlowWidth;
            float _GlowIntensity;
            float _ShadowStrength;
            float4 _GridOffset;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float fogFactor : TEXCOORD2;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = pos.positionCS;
                OUT.positionWS = pos.positionWS;
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.fogFactor = ComputeFogFactor(pos.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // 壁の向きに応じて、マス目を描く平面を選ぶ
                float3 p = IN.positionWS - _GridOffset.xyz;
                float3 n = abs(normalize(IN.normalWS));
                float2 uv = (n.y >= n.x && n.y >= n.z) ? p.xz
                          : (n.x >= n.z)               ? p.zy
                          :                              p.xy;

                float cell = max(_CellSize, 1e-4);
                float2 fw = max(fwidth(uv), 1e-5);

                // 最寄りの線までの距離（Unity単位）
                float2 dist = abs(frac(uv / cell + 0.5) - 0.5) * cell;

                // 線本体（画素の幅でなめらかにしてギザギザを防ぐ）
                float halfW = _LineWidth * 0.5;
                float2 core = 1.0 - smoothstep(halfW - fw, halfW + fw, dist);
                // 線のにじみ
                float2 glow = exp(-dist / max(_GlowWidth, 1e-4));

                // 遠くでマス目が画素より細かくなるとちらつく（モアレ）ので、平均の明るさに置き換える
                float2 fade = saturate(fw / cell * 2.0 - 0.5);
                core = lerp(core, saturate(_LineWidth / cell), fade);
                glow = lerp(glow, saturate(_GlowWidth * 2.0 / cell), fade);

                float lineMask = max(core.x, core.y);
                float glowMask = max(glow.x, glow.y) * _GlowIntensity;

                // キャラの落ち影（線は光っているので影の影響を半分にする）
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(IN.positionWS));
                float shadow = lerp(1.0, mainLight.shadowAttenuation, _ShadowStrength);
                float lineShadow = lerp(1.0, shadow, 0.5);

                float3 color = _BaseColor.rgb * shadow
                             + _LineColor.rgb * saturate(lineMask + glowMask) * lineShadow;
                color = MixFog(color, IN.fogFactor);
                return half4(color, 1.0);
            }
            ENDHLSL
        }

        // 深度テクスチャ（ポストエフェクトなど）用
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 vert(float4 positionOS : POSITION) : SV_POSITION
            {
                return TransformObjectToHClip(positionOS.xyz);
            }

            half frag() : SV_Target { return 0; }
            ENDHLSL
        }

        // SSAO などの法線テクスチャ用
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                return half4(normalize(IN.normalWS), 0.0);
            }
            ENDHLSL
        }
    }
}
