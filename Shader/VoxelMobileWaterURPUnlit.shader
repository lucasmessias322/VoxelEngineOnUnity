Shader "Voxel/URP Mobile/Water Unlit Static"
{
    Properties
    {
        [MainTexture][NoScaleOffset] _MainTexture("Water Still Spritesheet", 2D) = "white" {}
        [MainColor] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        _Color("Water Tint", Color) = (0.0, 0.376, 1.0, 1.0)
        _alpha("Water Alpha", Range(0.0, 1.0)) = 1.0
        _Maxalpha("Max Alpha", Range(0.0, 1.0)) = 0.99

        [Header(Cutout)]
        [ToggleUI] _AlphaClip("Alpha Clip", Float) = 0.0
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5

        [HideInInspector] _Cull("__cull", Float) = 0.0
        [HideInInspector] _AlphaToMask("__alphaToMask", Float) = 0.0
        [HideInInspector][NoScaleOffset] _Atlas("Atlas", 2D) = "white" {}
        [HideInInspector][NoScaleOffset] _BaseMap("Base Map", 2D) = "white" {}
        [HideInInspector][NoScaleOffset] _MainTex("Main Tex", 2D) = "white" {}
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        LOD 50
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite On
        Cull Off

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }
            AlphaToMask [_AlphaToMask]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex WaterVertex
            #pragma fragment WaterFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _Color;
                float _alpha;
                float _Maxalpha;
                float _AlphaClip;
                float _Cutoff;
            CBUFFER_END

            float4 _FogColorSurface;
            float _FogStart;
            float _FogEnd;

            TEXTURE2D(_MainTexture);
            SAMPLER(sampler_MainTexture);
            float4 _MainTexture_TexelSize;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv0 : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 localUV : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float2 RepeatTileUV(float2 uv)
            {
                return uv - floor(uv);
            }

            float2 ResolveStaticWaterUV(float2 localUV)
            {
                float2 tiledUV = RepeatTileUV(localUV);
                float textureWidth = max(_MainTexture_TexelSize.z, 1.0);
                float textureHeight = max(_MainTexture_TexelSize.w, 1.0);
                float frameSizeY = saturate(textureWidth / textureHeight);
                frameSizeY = max(frameSizeY, rcp(textureHeight));
                float frameOffsetY = 1.0 - frameSizeY;
                return float2(tiledUV.x, frameOffsetY + tiledUV.y * frameSizeY);
            }

            void ApplyVoxelAlphaClip(half alpha)
            {
                if (_AlphaClip > 0.5)
                    clip(alpha - (half)_Cutoff);
            }

            half3 ApplyVoxelFog(half3 color, float3 positionWS)
            {
                float fogRange = max(0.0001, _FogEnd - _FogStart);
                float cameraDistance = distance(GetCameraPositionWS(), positionWS);
                half fogFactor = (half)saturate((cameraDistance - _FogStart) / fogRange);
                return lerp(color, (half3)_FogColorSurface.rgb, fogFactor);
            }

            Varyings WaterVertex(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.localUV = input.uv0;
                return output;
            }

            half4 WaterFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 sheet = SAMPLE_TEXTURE2D(_MainTexture, sampler_MainTexture, ResolveStaticWaterUV(input.localUV));
                half3 color = sheet.rgb * (half3)_BaseColor.rgb * (half3)_Color.rgb;
                half alpha = saturate((half)_BaseColor.a * (half)_Color.a * (half)_alpha);
                alpha = min(alpha, (half)_Maxalpha);
                ApplyVoxelAlphaClip(alpha);

                color = ApplyVoxelFog(color, input.positionWS);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    Fallback "Hidden/Universal Render Pipeline/FallbackError"
}
