using ProtoBuf;
using VRageMath;

namespace Automata.Util
{
    [ProtoContract]
    public struct Vector3IData
    {
        [ProtoMember(1)] public int X;
        [ProtoMember(2)] public int Y;
        [ProtoMember(3)] public int Z;

        public Vector3IData(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static Vector3IData FromVector3I(Vector3I v)
        {
            return new Vector3IData(v.X, v.Y, v.Z);
        }

        public Vector3I ToVector3I()
        {
            return new Vector3I(X, Y, Z);
        }
    }
}
