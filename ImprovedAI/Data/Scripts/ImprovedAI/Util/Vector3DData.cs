using ProtoBuf;
using VRageMath;

namespace ImprovedAI.Util
{
    [ProtoContract]
    public struct Vector3DData
    {
        [ProtoMember(1)] public double X;
        [ProtoMember(2)] public double Y;
        [ProtoMember(3)] public double Z;

        public static Vector3DData FromVector3D(Vector3D v)
        {
            return new Vector3DData
            {
                X = v.X,
                Y = v.Y,
                Z = v.Z
            };
        }

        public Vector3D ToVector3D()
        {
            return new Vector3D(X, Y, Z);
        }
    }
}
