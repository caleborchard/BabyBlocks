using UnityEngine;

namespace BabyBlocks
{
    // Shared GUIStyle construction for the drag-and-drop palette panels
    // (PropPalette, MaterialConstructionPanel): both lay out a grid of
    // boxed items and a semi-transparent "ghost" copy that follows the
    // cursor while dragging.
    static class GuiStyleHelpers
    {
        public static void EnsureItemAndGhostStyles(ref GUIStyle itemStyle, ref GUIStyle ghostStyle)
        {
            if (itemStyle == null)
            {
                var padding = new RectOffset { left = 4, right = 4, top = 4, bottom = 4 };
                itemStyle = new GUIStyle(GUI.skin.box)
                {
                    wordWrap = true,
                    alignment = TextAnchor.MiddleCenter,
                    clipping = TextClipping.Clip,
                    padding = padding
                };
            }

            if (ghostStyle == null)
                ghostStyle = new GUIStyle(itemStyle);
        }
    }
}
