using VRage;
using VRageMath;
using VRage.Utils;

namespace Automata.MiningSurveyor
{
    /// <summary>
    /// One indexed ore deposit sample stored by <see cref="IAIMiningSurveyor"/>.
    /// </summary>
    public sealed class MiningDepositCacheEntry
    {
        public MyStringHash OreSubtype;
        public Vector3D Position;
        public MyFixedPoint EstimatedMass;
        public bool PendingPublish;
    }
}
