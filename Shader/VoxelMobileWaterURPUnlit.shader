Shader "Voxel/URP Mobile/Water Unlit Static"
{
    Properties
    {
        [MainTexture][NoScaleOffset] _MainTexture("Water Still Spritesheet", 2D) = "white" {}
        [MainColor] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        _Color("Water Tint", Color) = (0.0, 0.376, 1.0, 1.0)
        _alpha("Water Alpha", Range(0.0, 1.0)) = 1.0
        _Maxalpha("Max Alpha", Range(0.0, 1.0)) = 0.99

        [Header(Animation)]
        _FrameDuration("Frame Duration", Range(0.01, 1.0)) = 0.1
        _FlowUVSpeed("Animation Drift", Range(0.0, 1.0)) = 0.2

        _DepthShallowColor("Shallow Color", Color) = (0.0, 0.3647059, 0.45098042, 1.0)
        _DepthDeepColor("Deep Color", Color) = (0.0, 0.3643713, 0.4528302, 1.0)
        _DepthStartDistance("Depth Offset", Range(0.0, 8.0)) = 6.0
        _DepthColorDistance("Depth Range", Range(0.05, 32.0)) = 5.5
        _DepthAbsorptionStrength("Depth Curve", Range(0.25, 4.0)) = 1.25
        _DepthShallowAlpha("Shallow Opacity", Range(0.0, 1.0)) = 0.34
        _DepthDeepAlpha("Deep Opacity", Range(0.0, 1.0)) = 0.94

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
        Blend One OneMinusSrcAlpha
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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _Color;
                float4 _DepthShallowColor;
                float4 _DepthDeepColor;
                float _alpha;
                float _Maxalpha;
                float _FrameDuration;
                float _FlowUVSpeed;
                float _DepthStartDistance;
                float _DepthColorDistance;
                float _DepthAbsorptionStrength;
                float _DepthShallowAlpha;
                float _DepthDeepAlpha;
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

            float GetWaterFrameCount()
            {
                float textureWidth = max(_MainTexture_TexelSize.z, 1.0);
                float textureHeight = max(_MainTexture_TexelSize.w, 1.0);
                return max(floor(textureHeight / textureWidth + 0.5), 1.0);
            }

            float GetWrappedFrameIndex(float frameStep, float frameCount)
            {
                return frameStep - frameCount * floor(frameStep / frameCount);
            }

            float2 ResolveAnimatedWaterUV(float2 localUV, float frameIndex, float frameCount)
            {
                float2 tiledUV = RepeatTileUV(localUV);
                float frameSizeY = rcp(frameCount);
                float frameOffsetY = 1.0 - frameSizeY;
                frameOffsetY -= frameIndex * frameSizeY;
                return float2(tiledUV.x, frameOffsetY + tiledUV.y * frameSizeY);
            }

            half4 SampleAnimatedWaterPhase(float2 localUV, float framePhase, float frameCount)
            {
                float baseFrame = floor(framePhase);
                float nextFrame = baseFrame + 1.0;
                float frameBlend = smoothstep(0.0, 1.0, frac(framePhase));
                float wrappedBaseFrame = GetWrappedFrameIndex(baseFrame, frameCount);
                float wrappedNextFrame = GetWrappedFrameIndex(nextFrame, frameCount);

                half4 sampleA = SAMPLE_TEXTURE2D(_MainTexture, sampler_MainTexture, ResolveAnimatedWaterUV(localUV, wrappedBaseFrame, frameCount));
                half4 sampleB = SAMPLE_TEXTURE2D(_MainTexture, sampler_MainTexture, ResolveAnimatedWaterUV(localUV, wrappedNextFrame, frameCount));
                return lerp(sampleA, sampleB, (half)frameBlend);
            }

            half4 SampleAnimatedWaterTexture(float2 localUV, float3 positionWS)
            {
                float frameCount = GetWaterFrameCount();
                float frameDuration = max(_FrameDuration, 0.0001);
                float basePhase = _Time.y / frameDuration;
                float driftStrength = saturate(_FlowUVSpeed);
                float2 worldXZ = positionWS.xz;

                float2 uvOffsetA = float2(
                    sin(worldXZ.y * 0.17 + _Time.y * 0.33),
                    cos(worldXZ.x * 0.14 - _Time.y * 0.29)) * (0.035 * driftStrength);

                float2 uvOffsetB = float2(
                    cos(dot(worldXZ, float2(0.11, -0.15)) + _Time.y * 0.23),
                    sin(dot(worldXZ, float2(-0.09, 0.13)) - _Time.y * 0.27)) * (0.055 * driftStrength);

                float phaseA = basePhase + dot(worldXZ, float2(0.08, 0.11));
                float phaseB = basePhase * 0.93 + dot(worldXZ, float2(-0.06, 0.09)) + frameCount * 0.37;

                half4 sampleA = SampleAnimatedWaterPhase(localUV + uvOffsetA, phaseA, frameCount);
                half4 sampleB = SampleAnimatedWaterPhase(localUV - uvOffsetB, phaseB, frameCount);

                half crossBlend = 0.5h + 0.5h * sin((half)(dot(worldXZ, float2(0.05, 0.04)) + _Time.y * 0.41));
                crossBlend = lerp(0.35h, 0.65h, crossBlend);
                return lerp(sampleA, sampleB, crossBlend);
            }

            void ApplyVoxelAlphaClip(half alpha)
            {
                if (_AlphaClip > 0.5)
                    clip(alpha - (half)_Cutoff);
            }

            float2 GetWaterScreenUV(float4 positionCS)
            {
                #if defined(UNITY_PRETRANSFORM_TO_DISPLAY_ORIENTATION)
                    float2 preRotatedScreenSpaceUV = GetNormalizedScreenSpaceUV(positionCS);
                    switch (UNITY_DISPLAY_ORIENTATION_PRETRANSFORM)
                    {
                        default:
                        case UNITY_DISPLAY_ORIENTATION_PRETRANSFORM_0:
                            return preRotatedScreenSpaceUV;
                        case UNITY_DISPLAY_ORIENTATION_PRETRANSFORM_90:
                            return float2(1.0 - preRotatedScreenSpaceUV.y, preRotatedScreenSpaceUV.x);
                        case UNITY_DISPLAY_ORIENTATION_PRETRANSFORM_180:
                            return float2(1.0 - preRotatedScreenSpaceUV.x, 1.0 - preRotatedScreenSpaceUV.y);
                        case UNITY_DISPLAY_ORIENTATION_PRETRANSFORM_270:
                            return float2(preRotatedScreenSpaceUV.y, 1.0 - preRotatedScreenSpaceUV.x);
                    }
                #else
                    return GetNormalizedScreenSpaceUV(positionCS);
                #endif
            }

            float GetWaterDepthDifference(float2 normalizedScreenSpaceUV, float3 positionWS)
            {
                float rawSceneDepth = SampleSceneDepth(normalizedScreenSpaceUV);
                float sceneEyeDepth = (unity_OrthoParams.w == 0.0) ? LinearEyeDepth(rawSceneDepth, _ZBufferParams) : LinearDepthToEyeDepth(rawSceneDepth);
                float surfaceEyeDepth = LinearEyeDepth(positionWS, GetWorldToViewMatrix());
                return max(sceneEyeDepth - surfaceEyeDepth, 0.0);
            }

            half GetWaterDepthFactor(float2 normalizedScreenSpaceUV, float3 positionWS)
            {
                float waterDepth = GetWaterDepthDifference(normalizedScreenSpaceUV, positionWS);
                float depthOffset = max(_DepthStartDistance, 0.0);
                float depthRange = max(_DepthColorDistance, 0.0001);
                float normalizedDepth = saturate(max(waterDepth - depthOffset, 0.0) / depthRange);
                float depthCurve = max(_DepthAbsorptionStrength, 0.0001);
                return (half)pow(normalizedDepth, depthCurve);
            }

            half3 GetWaterDepthTint(half depthFactor)
            {
                return lerp((half3)_DepthShallowColor.rgb, (half3)_DepthDeepColor.rgb, depthFactor);
            }

            half GetWaterDepthAlpha(half depthFactor)
            {
                return lerp((half)_DepthShallowAlpha, (half)_DepthDeepAlpha, depthFactor);
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

                half4 sheet = SampleAnimatedWaterTexture(input.localUV, input.positionWS);
                half depthFactor = GetWaterDepthFactor(GetWaterScreenUV(input.positionCS), input.positionWS);
                half3 depthTint = GetWaterDepthTint(depthFactor);
                half depthAlpha = GetWaterDepthAlpha(depthFactor);

                half3 color = sheet.rgb * (half3)_BaseColor.rgb * (half3)_Color.rgb * depthTint;
                half alpha = saturate((half)_BaseColor.a * (half)_Color.a * (half)_alpha);
                alpha = saturate(alpha * depthAlpha);
                alpha = min(alpha, (half)_Maxalpha);
                ApplyVoxelAlphaClip(alpha);

                color = ApplyVoxelFog(color, input.positionWS);
                return half4(color * alpha, alpha);
            }
            ENDHLSL
        }
    }

    Fallback "Hidden/Universal Render Pipeline/FallbackError"
}