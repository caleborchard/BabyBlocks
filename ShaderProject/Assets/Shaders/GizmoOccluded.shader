// Drawn for each gizmo handle as a SECOND pass (queue 4001, after the solid queue-4000 pass).
// Shows a 4×4 checkerboard only where the handle is behind scene geometry.
// Uses _BabyBlocksDepthCopy (captured from mainCam at AfterEverything) instead of GPU ZTest
// so it never triggers on pixels where another gizmo part is in front — only scene geometry counts.
Shader "Hidden/BabyBlocks/GizmoOccluded"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        // Plain RFloat copy of scene depth, written by DepthCapture.shader.
        // Set globally via Shader.SetGlobalTexture so all gizmo occluded materials share it.
        _BabyBlocksDepthCopy ("Scene Depth Copy", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "Queue" = "Overlay" }
        ZWrite Off
        // ZTest LessEqual: only draw where this fragment is the frontmost gizmo part at this pixel.
        // The solid pass (queue 4000) already wrote gizmo depth; occluded (queue 4001) only passes
        // where its depth equals the solid depth (same mesh/transform = bit-identical clip depth).
        // Gizmo parts behind other gizmo parts fail ZTest and produce no checker — only scene
        // geometry occlusion (tested in the shader via _BabyBlocksDepthCopy) triggers it.
        ZTest LessEqual
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4    _Color;
            sampler2D _BabyBlocksDepthCopy;

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos   : SV_POSITION;
                float  depth : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                // clip.z/clip.w is [0,1] on DX (UNITY_REVERSED_Z: 1=near, 0=far).
                // This matches the values stored in _BabyBlocksDepthCopy.
                o.depth = o.pos.z / o.pos.w;
                #if !defined(UNITY_REVERSED_Z)
                    // OpenGL NDC Z is [-1,1]; convert to [0,1] to match the depth copy.
                    o.depth = o.depth * 0.5 + 0.5;
                #endif
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // SV_POSITION.xy are screen pixel coords: (0,0) = top-left on DX.
                // _BabyBlocksDepthCopy is camera-pixel-sized and uses the same top-left
                // UV origin on DX, so dividing by _ScreenParams.xy gives the correct UV.
                float2 screenUV = float2(i.pos.x / _ScreenParams.x, i.pos.y / _ScreenParams.y);
                float sceneD = tex2D(_BabyBlocksDepthCopy, screenUV).r;
                float fragD  = i.depth;

                // Occluded = gizmo fragment is behind scene geometry.
                // With reversed Z, larger value = closer to camera.
                // sceneD > fragD + bias → scene is clearly in front of gizmo → gizmo occluded.
                #if defined(UNITY_REVERSED_Z)
                    bool occluded = sceneD > fragD + 0.001;
                #else
                    bool occluded = sceneD < fragD - 0.001;
                #endif

                if (!occluded) discard;

                // 4×4 screen-space checkerboard — SV_POSITION.xy are pixel coords,
                // matching the same pattern in SelectionOutline for visual consistency.
                float checker = fmod(floor(i.pos.x / 2.0) + floor(i.pos.y / 2.0), 2.0);
                return checker < 0.5
                    ? fixed4(_Color.rgb * 0.35, 1.0)
                    : fixed4(0, 0, 0, 1.0);
            }
            ENDCG
        }
    }
}
