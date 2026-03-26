Shader "ChromaKeyURP/Unlit/Cutout"
{
    Properties
    {
        [Header(Material)]
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Texture", 2D) = "white" {}
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Culling", Float) = 2

        [Header(Chroma Key)]
        _ChromaKeyColor ("Key Color", Color) = (0,1,0,1)
        _ChromaKeyHueRange ("Hue Range", Range(0.0001, 1)) = 0.1
        _ChromaKeySaturationRange ("Saturation Range", Range(0.0001, 1)) = 0.5
        _ChromaKeyBrightnessRange ("Brightness Range", Range(0.0001, 1)) = 0.5

        [Header(Cutout)]
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalPipeline"
            "Queue"="AlphaTest"
            "RenderType"="TransparentCutout"
            "IgnoreProjector"="True"
        }

        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode"="UniversalForward" }

            Cull [_Cull]
            ZWrite On
            Blend One Zero

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float4 _MainTex_ST;

                float4 _ChromaKeyColor;
                float  _ChromaKeyHueRange;
                float  _ChromaKeySaturationRange;
                float  _ChromaKeyBrightnessRange;

                float  _Cutoff;
            CBUFFER_END

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            float3 RGBToHSV(float3 c)
            {
                float4 K = float4(0., -1./3., 2./3., -1.);
                float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
                float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
                float d = q.x - min(q.w, q.y);
                float e = 1e-10;
                return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
            }

            float HueDistance(float h1, float h2)
            {
                float dh = abs(h1 - h2);
                return min(dh, 1.0 - dh);
            }

            float ComputeAlphaFactor(float3 rgb)
            {
                float3 hsv    = RGBToHSV(rgb);
                float3 keyHsv = RGBToHSV(_ChromaKeyColor.rgb);

                float dHue = HueDistance(hsv.x, keyHsv.x) / max(_ChromaKeyHueRange, 1e-4);
                float dSat = abs(hsv.y - keyHsv.y)      / max(_ChromaKeySaturationRange, 1e-4);
                float dVal = abs(hsv.z - keyHsv.z)      / max(_ChromaKeyBrightnessRange, 1e-4);

                float d = max(dHue, max(dSat, dVal));
                return saturate(d); // 0 near key, 1 away from key
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionHCS = pos.positionCS;
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv) * _Color;
                col.a *= ComputeAlphaFactor(col.rgb);
                clip(col.a - _Cutoff);
                return col;
            }
            ENDHLSL
        }
    }
}
