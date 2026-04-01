Shader "Hidden/VecEnv/DepthObservation"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "DepthObservation"

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag

            #include "UnityCG.cginc"

            // Sample the active observation camera's depth texture.
            // _LastCameraDepthTexture can point to another camera's depth buffer, which leaves our capture empty.
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            float _DepthMetersMin;
            float _DepthMetersMax;

            fixed4 frag(v2f_img i) : SV_Target
            {
                const float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                const float eyeDepth = LinearEyeDepth(rawDepth);
                const float depthRange = max(0.0001, _DepthMetersMax - _DepthMetersMin);
                // Emit higher values for nearby pixels and fade toward 0 as depth approaches the max range.
                const float depth01 = 1.0 - saturate((eyeDepth - _DepthMetersMin) / depthRange);
                return fixed4(depth01, depth01, depth01, 1.0);
            }
            ENDCG
        }
    }
}
