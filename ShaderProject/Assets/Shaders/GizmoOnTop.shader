// Solid, always-on-top gizmo handle (the center free-move sphere / free-scale cube).
// ZTest Always is BAKED into the shader so it renders over scene geometry and the other
// gizmo parts regardless of unity_GUIZTestMode globals (which don't reliably survive into
// Graphics.DrawMesh transparent-queue draws).  Cull Off so the sphere reads as fully solid
// from every angle.
Shader "Hidden/BabyBlocks/GizmoOnTop"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "Queue" = "Overlay" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;

            struct v2f { float4 pos : SV_POSITION; };

            v2f vert(appdata_base v)
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
