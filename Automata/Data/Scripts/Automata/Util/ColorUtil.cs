using System;
using VRage.Game.ModAPI;
using VRageMath;

namespace Automata.Util
{
    public static class ColorUtil
    {
        public static bool ColorMatch(IMySlimBlock block, Vector3 matchColor)
        {
            var blockHSV = block.ColorMaskHSV;
            return Math.Abs(blockHSV.X - matchColor.X) < 0.01f &&
                   Math.Abs(blockHSV.Y - matchColor.Y) < 0.01f &&
                   Math.Abs(blockHSV.Z - matchColor.Z) < 0.01f;
        }
    }
}
