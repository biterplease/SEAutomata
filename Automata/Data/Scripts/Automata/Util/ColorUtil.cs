using System;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace Automata.Util
{
    public static class ColorUtil
    {
        /// <summary>Packed defaults (Color.PackedValue, ABGR): grind red RGB(255,0,0), weld-ignore magenta RGB(255,0,255).</summary>
        public const uint DEFAULT_GRIND_COLOR = 0xFF0000FF;
        public const uint DEFAULT_WELD_IGNORE_COLOR = 0xFFFF00FF;

        /// <summary>
        /// The block is painted exactly this colour: its colour mask turned back into the RGB the colour picker shows, byte
        /// for byte (no tolerance). The mask is stored as an HSV offset in floats, so comparing those directly would
        /// need an epsilon; the 0-255 channels don't.
        /// </summary>
        public static bool SameColor(IMySlimBlock block, Color color)
        {
            if (block == null) return false;
            Color painted = MyColorPickerConstants.HSVOffsetToHSV(block.ColorMaskHSV).HSVtoColor();
            return painted.R == color.R && painted.G == color.G && painted.B == color.B;
        }

        public static bool ColorMatch(IMySlimBlock block, Vector3 matchColor)
        {
            var blockHSV = block.ColorMaskHSV;
            return Math.Abs(blockHSV.X - matchColor.X) < 0.01f &&
                   Math.Abs(blockHSV.Y - matchColor.Y) < 0.01f &&
                   Math.Abs(blockHSV.Z - matchColor.Z) < 0.01f;
        }
    }
}
