// Drawn for each gizmo handle as a SECOND pass (queue 4001, after the solid queue-4000 pass).
// Shows a 4×4 checkerboard only where the handle is behind scene geometry.
// ZTest LessEqual against the gizmo cam's depth buffer (written by the solid pass at queue 4000)
// ensures only the frontmost gizmo part at each pixel can draw — a part hidden behind another
// gizmo part produces a smaller hw depth value and fails the test. Scene geometry occlusion is
// then tested in the shader via _BabyBlocksDepthCopy.
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
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4    _Color;
            sampler2D _BabyBlocksDepthCopy;
            float     _BabyBlocksCamNear; // mainCam.nearClipPlane, set globally each frame

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

                // Convert to approximate eye distance via eye ≈ near/hwDepth (reversed-Z).
                // overlayCam.near == mainCam.near (synced in SyncCamToMain), so the
                // conversion is consistent for both fragD (overlayCam) and sceneD (mainCam).
                // 5 cm tolerance is distance-independent and absorbs sampling jitter.
                #if defined(UNITY_REVERSED_Z)
                    float sceneEye = _BabyBlocksCamNear / max(sceneD, 1e-7);
                    float fragEye  = _BabyBlocksCamNear / max(fragD,  1e-7);
                    bool occluded  = sceneEye < fragEye - 0.05;
                #else
                    bool occluded = sceneD < fragD - fragD * 0.01;
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
