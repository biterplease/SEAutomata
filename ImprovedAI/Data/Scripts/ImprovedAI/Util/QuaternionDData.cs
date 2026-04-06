using ProtoBuf;
using VRageMath;

namespace ImprovedAI.Util
{
    [ProtoContract]
    public struct QuaternionDData
    {
        [ProtoMember(1)] public double X;
        [ProtoMember(2)] public double Y;
        [ProtoMember(3)] public double Z;
        [ProtoMember(4)] public double W;

        public static QuaternionDData FromQuaternion(QuaternionD q)
        {
            return new QuaternionDData
            {
                X = q.X,
                Y = q.Y,
                Z = q.Z,
                W = q.W
            };
        }

        public QuaternionD ToQuaternionD()
        {
            return new QuaternionD(X, Y, Z, W);
        }
    }
}
