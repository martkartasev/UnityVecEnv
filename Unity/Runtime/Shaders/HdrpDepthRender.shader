Shader "Hidden/VecEnv/HdrpDepthRender"
{
    Properties
    {
        _DepthMetersMin("Depth Meters Min", Float) = 0
        _DepthMetersMax("Depth Meters Max", Float) = 20
    }

    HLSLINCLUDE

    #pragma target 4.5
    #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch switch2
    #pragma multi_compile_instancing
    #pragma multi_compile _ DOTS_INSTANCING_ON

    #define ATTRIBUTES_NEED_TEXCOORD0
    #define ATTRIBUTES_NEED_NORMAL
    #define ATTRIBUTES_NEED_TANGENT
    #define VARYINGS_NEED_TEXCOORD0
    #define VARYINGS_NEED_TANGENT_TO_WORLD

    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassRenderers.hlsl"

    #pragma vertex Vert
    #pragma fragment Frag

    float _DepthMetersMin;
    float _DepthMetersMax;

    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }

        Pass
        {
            Name "DepthNormalizedPass"

            Blend Off
            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM

            void GetSurfaceAndBuiltinData(
                FragInputs fragInputs,
                float3 viewDirection,
                inout PositionInputs posInput,
                out SurfaceData surfaceData,
                out BuiltinData builtinData)
            {
                ZERO_BUILTIN_INITIALIZE(builtinData);
                builtinData.opacity = 1;
                builtinData.emissiveColor = 0;

                float eyeDepth = LinearEyeDepth(fragInputs.positionSS.z, _ZBufferParams);
                float depthRange = max(0.0001, _DepthMetersMax - _DepthMetersMin);
                float depth01 = saturate((eyeDepth - _DepthMetersMin) / depthRange);

                surfaceData.color = depth01.xxx;
                surfaceData.normalWS = 0.0;
            }

            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPassForwardUnlit.hlsl"

            ENDHLSL
        }
    }

    Fallback Off
}
