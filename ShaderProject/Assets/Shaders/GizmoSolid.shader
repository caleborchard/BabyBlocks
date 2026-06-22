// Solid-color gizmo handle pass. Writes stencil=2 at every pixel where this fragment
// wins the depth test (is the frontmost gizmo part). GizmoOccluded uses Stencil Equal 2
// so it only runs at those same frontmost pixels, preventing checker from appearing where
// one gizmo part is hidden behind another.
Shader "Hidden/BabyBlocks/GizmoSolid"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "Queue" = "Overlay" }
        ZTest LEqual
        ZWrite On
        Stencil
        {
            Ref 2
            Comp Always
            Pass Replace
            ZFail Keep
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;

            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos : SV_POSITION; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target { return _Color; }
            ENDCG
        }
    }
}
