// BabyBlocks mod — tint overlay shader.
// Renders the prop's mesh a second time via Graphics.DrawMesh at queue 2999 (after all
// opaque geometry).  Blend DstColor Zero multiplies whatever is already in the
// framebuffer by _TintColor, producing a per-prop color tint without touching the
// original materials.
Shader "BabyBlocks/TintOverlay"
{
    Properties
    {
        _TintColor ("Tint", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags
        {
            "RenderType"      = "Transparent"
            "Queue"           = "Transparent-1"
            "RenderPipeline"  = "UniversalPipeline"
        }

        Pass
        {
            Name "TintOverlay"
            Tags { "LightMode" = "UniversalForward" }

            // Multiply blend: output = src * dst  (tintColor * alreadyRenderedColor)
            Blend DstColor Zero
            ZWrite Off
            ZTest  LEqual
            Cull   Off      // tint back-faces too (glass, thin meshes, etc.)

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _TintColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                return _TintColor;
            }
            ENDHLSL
        }
    }
}
