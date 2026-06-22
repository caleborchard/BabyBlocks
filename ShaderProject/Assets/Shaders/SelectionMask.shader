// Renders selected objects into a mask RenderTexture.
// RGB = selection color (outline/player color), A = NDC depth (biased so 0 means "not selected").
// ZTest LessEqual against the mask RT's own depth so the frontmost selected object wins
// when two selected objects overlap. Non-selected scene objects never render here.
Shader "Hidden/BabyBlocks/SelectionMask"
{
    Properties
    {
        _Color ("Color", Color) = (1, 0.85, 0.1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        ZWrite On
        ZTest LEqual
        Cull Back

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _Color;

            struct appdata { float4 vertex : POSITION; };
            struct v2f     { float4 pos : SV_POSITION; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            // SV_POSITION.z in the fragment is the same depth value written to the depth buffer,
            // matching what _CameraDepthTexture contains — so the alpha channel can be compared
            // directly against scene depth in the post-process pass.
            // Bias by 0.0001 so alpha is never exactly 0 (which means "not selected" after clear).
            float4 frag(v2f i) : SV_Target
            {
                float depth = max(0.0001, i.pos.z);
                return float4(_Color.rgb, depth);
            }
            ENDCG
        }
    }
}
