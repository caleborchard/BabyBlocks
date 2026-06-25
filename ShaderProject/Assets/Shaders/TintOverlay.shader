// BabyBlocks mod — tint overlay shader.
// Rendered via Graphics.DrawMesh at queue 2999 (after all opaque geometry).
// Blend DstColor Zero multiplies whatever is already in the framebuffer by _TintColor,
// producing a per-prop color tint without touching the original materials or shaders.
Shader "BabyBlocks/TintOverlay"
{
    Properties
    {
        _TintColor ("Tint", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent-1" }

        // Multiply blend: output = tintColor * alreadyRenderedColor
        Blend DstColor Zero
        ZWrite Off
        ZTest  LEqual
        Cull   Off

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _TintColor;

            struct appdata { float4 vertex : POSITION; };
            struct v2f     { float4 pos    : SV_POSITION; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return _TintColor;
            }
            ENDCG
        }
    }
}
