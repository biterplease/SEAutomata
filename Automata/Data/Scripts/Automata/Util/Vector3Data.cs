using ProtoBuf;
using VRageMath;

namespace Automata.Util
{
    [ProtoContract]
    public struct Vector3Data
    {
        [ProtoMember(1)] public float X;
        [ProtoMember(2)] public float Y;
        [ProtoMember(3)] public float Z;

        public Vector3Data(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static Vector3Data FromVector3(Vector3 v)
        {
            return new Vector3Data
            {
                X = v.X,
                Y = v.Y,
                Z = v.Z
            };
        }

        public Vector3 ToVector3()
        {
            return new Vector3(X, Y, Z);
        }
    }
}
