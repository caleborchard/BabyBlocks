// Copies _CameraDepthTexture into a plain RFloat render texture so other shaders
// can sample it with a regular sampler2D.  Must run inside mainCam's pipeline so
// that Unity's internal depth-texture binding is active.
Shader "Hidden/BabyBlocks/DepthCapture"
{
    Properties { _MainTex ("", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            // UNITY_DECLARE_DEPTH_TEXTURE uses the correct platform sampler type
            // (Texture2D_float + SamplerState on DX11) so tex2D/SAMPLE_DEPTH_TEXTURE
            // can actually read the depth-stencil format texture.
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            float frag(v2f_img i) : SV_Target
            {
                return SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
            }
            ENDCG
        }
    }
}
