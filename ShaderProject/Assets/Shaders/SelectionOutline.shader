// Screen-space 1px selection outline, UE4-style.
// _MainTex IS the selection mask (set by CommandBuffer.Blit's source argument).
// RGB = outline color, A = NDC depth (bias 0.0001 so 0 means "not selected").
// Alpha-blended: transparent pixels preserve the destination — no scene capture needed.
Shader "Hidden/BabyBlocks/SelectionOutline"
{
    Properties
    {
        _MainTex            ("Selection Mask (via Blit)",  2D)          = "black" {}
        // Plain RFloat copy of the scene depth, written by DepthCapture.shader running
        // inside mainCam's pipeline.  Sampled as a regular float texture (no depth-sampler
        // restrictions) so SetTexture works correctly in IL2CPP.
        _BabyBlocksDepthCopy ("Scene Depth Copy",          2D)          = "black" {}
        _OutlineColor        ("Outline Color",              Color)       = (1, 0.85, 0.1, 1)
        _OccludedAlpha       ("Occluded Brightness",        Range(0, 1)) = 0.35
        _OutlineWidth        ("Outline Width (px)",         Range(1, 8)) = 2
        // 0=normal  1=force-dark (ignore depth, always use occluded shade)  2=sceneD×100 grayscale
        _DebugMode           ("Debug Mode",                 Range(0, 2)) = 0
        // Camera viewport rect in normalized screen coords (x,y,width,height).
        // Remaps the full-screen mask UV to viewport-relative UV for the depth copy,
        // which is generated at viewport resolution (not full-screen).
        _ViewportRect        ("Viewport Rect (x,y,w,h)",   Vector)      = (0, 0, 1, 1)
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4    _MainTex_TexelSize;   // set by Blit: (1/w, ±1/h, w, h); y<0 on DX when RT is flipped
            sampler2D _BabyBlocksDepthCopy; // plain RFloat copy of scene depth
            float4    _OutlineColor;
            float     _OccludedAlpha;
            float     _OutlineWidth;
            float4    _ViewportRect;
            float     _DebugMode;

            float4 frag(v2f_img i) : SV_Target
            {
                float2 uv = i.uv;

                // On DX, render textures have Y stored top-down while UV convention is bottom-up.
                // _MainTex_TexelSize.y is negative when the source RT needs a V-flip.
                #if UNITY_UV_STARTS_AT_TOP
                if (_MainTex_TexelSize.y < 0) uv.y = 1.0 - uv.y;
                #endif

                // Per-texel size; scaled by _OutlineWidth to control border thickness.
                float2 ts = abs(_MainTex_TexelSize.xy);
                float2 off = ts * _OutlineWidth;

                // ── Center + 8-way neighbors (cardinal + diagonal) ───────────────────
                float4 c  = tex2D(_MainTex, uv);
                float4 n1 = tex2D(_MainTex, uv + float2( 1,  0) * off);
                float4 n2 = tex2D(_MainTex, uv + float2(-1,  0) * off);
                float4 n3 = tex2D(_MainTex, uv + float2( 0,  1) * off);
                float4 n4 = tex2D(_MainTex, uv + float2( 0, -1) * off);
                float4 n5 = tex2D(_MainTex, uv + float2( 1,  1) * off);
                float4 n6 = tex2D(_MainTex, uv + float2(-1, -1) * off);
                float4 n7 = tex2D(_MainTex, uv + float2( 1, -1) * off);
                float4 n8 = tex2D(_MainTex, uv + float2(-1,  1) * off);

                // Alpha > 0 means "selected" (NDC depth stored there, biased so 0 = not selected)
                bool cSel  = c.a  > 0.00005;
                bool n1Sel = n1.a > 0.00005;
                bool n2Sel = n2.a > 0.00005;
                bool n3Sel = n3.a > 0.00005;
                bool n4Sel = n4.a > 0.00005;
                bool n5Sel = n5.a > 0.00005;
                bool n6Sel = n6.a > 0.00005;
                bool n7Sel = n7.a > 0.00005;
                bool n8Sel = n8.a > 0.00005;
                bool anyNSel = n1Sel || n2Sel || n3Sel || n4Sel || n5Sel || n6Sel || n7Sel || n8Sel;
                bool isEdge  = cSel != anyNSel;

                // Not an edge — fully transparent, preserve destination
                if (!isEdge) return float4(0, 0, 0, 0);

                // ── Pick color + depth from the nearest selected sample ───────────────
                float3 selColor = cSel  ? c.rgb
                    : n1Sel ? n1.rgb : n2Sel ? n2.rgb : n3Sel ? n3.rgb : n4Sel ? n4.rgb
                    : n5Sel ? n5.rgb : n6Sel ? n6.rgb : n7Sel ? n7.rgb : n8.rgb;
                float  selDepth = cSel  ? c.a
                    : n1Sel ? n1.a : n2Sel ? n2.a : n3Sel ? n3.a : n4Sel ? n4.a
                    : n5Sel ? n5.a : n6Sel ? n6.a : n7Sel ? n7.a : n8.a;
                float2 selUV    = cSel  ? uv
                    : n1Sel ? (uv + float2( 1,  0) * off) : n2Sel ? (uv + float2(-1,  0) * off)
                    : n3Sel ? (uv + float2( 0,  1) * off) : n4Sel ? (uv + float2( 0, -1) * off)
                    : n5Sel ? (uv + float2( 1,  1) * off) : n6Sel ? (uv + float2(-1, -1) * off)
                    : n7Sel ? (uv + float2( 1, -1) * off) : (uv + float2(-1,  1) * off);

                // ── Occlusion: is another object in front of the selected surface? ────
                // selUV is in full-screen UV space (0-1 = full screen width).
                // _BabyBlocksDepthCopy is viewport-relative (0-1 = viewport width).
                // Remap x into viewport UV space before sampling.
                float2 depthUV = float2(
                    (selUV.x - _ViewportRect.x) / _ViewportRect.z,
                    (selUV.y - _ViewportRect.y) / _ViewportRect.w
                );
                float sceneD = tex2D(_BabyBlocksDepthCopy, depthUV).r;
                // Bias 0.001: large enough to absorb precision differences between
                // the mask pass and the depth copy, avoiding false self-occlusion.
                #if defined(UNITY_REVERSED_Z)
                    bool occluded = sceneD > selDepth + 0.001;
                #else
                    bool occluded = sceneD < selDepth - 0.001;
                #endif

                // ── Debug modes ─────────────────────────────────────────────────────
                if (_DebugMode > 0.5 && _DebugMode < 1.5)
                    return float4(selColor * _OccludedAlpha, 1.0);
                if (_DebugMode > 1.5)
                    return float4(saturate(sceneD * 100.0), saturate(selDepth * 100.0), occluded ? 1.0 : 0.0, 1.0);

                // ── Visible outline ──────────────────────────────────────────────────
                if (!occluded)
                    return float4(selColor, 1.0);

                // ── Occluded outline: 4×4 screen-space checkerboard ─────────────────
                // Use i.uv (pre Y-flip) for pixel coords so the pattern is in the same
                // screen-space convention as the gizmo shader's SV_POSITION.
                float2 checkerPx = float2(
                    i.uv.x * _ScreenParams.x,
                    i.uv.y * _ScreenParams.y
                );
                float checker = fmod(floor(checkerPx.x / 2.0) + floor(checkerPx.y / 2.0), 2.0);
                // Odd cells: transparent hole. Even cells: darker shade.
                if (checker > 0.5)
                    return float4(0, 0, 0, 0);
                return float4(selColor * _OccludedAlpha, 1.0);
            }
            ENDCG
        }
    }
}
