using System;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace Automata.Util
{
    public static class ColorUtil
    {

        /// <summary>
        /// The block is painted exactly this color: its color mask turned back into the RGB the color picker shows, byte
        /// for byte (no tolerance). The mask is stored as an HSV offset in floats, so comparing those directly would
        /// need an epsilon; the 0-255 channels don't.
        /// </summary>
        public static bool SameColor(IMySlimBlock block, Color color)
        {
            if (block == null) return false;
            Color painted = MyColorPickerConstants.HSVOffsetToHSV(block.ColorMaskHSV).HSVtoColor();
            return painted.R == color.R && painted.G == color.G && painted.B == color.B;
        }

    }
}
