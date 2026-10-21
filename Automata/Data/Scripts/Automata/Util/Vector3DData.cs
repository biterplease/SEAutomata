using ProtoBuf;
using VRageMath;

namespace Automata.Util
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
        public static double DistanceSquared(Vector3DData value1, Vector3DData value2)
        {
            double num = value1.X - value2.X;
            double num2 = value1.Y - value2.Y;
            double num3 = value1.Z - value2.Z;
            return num * num + num2 * num2 + num3 * num3;
        }
        public static double DistanceSquared(Vector3DData value1, Vector3D value2)
        {
            double num = value1.X - value2.X;
            double num2 = value1.Y - value2.Y;
            double num3 = value1.Z - value2.Z;
            return num * num + num2 * num2 + num3 * num3;
        }

        public static double DistanceSquared(Vector3D value1, Vector3D value2)
        {
            double num = value1.X - value2.X;
            double num2 = value1.Y - value2.Y;
            double num3 = value1.Z - value2.Z;
            return num * num + num2 * num2 + num3 * num3;
        }
       public static double DistanceSquared(Vector3D value1, Vector3DData value2)
        {
            double num = value1.X - value2.X;
            double num2 = value1.Y - value2.Y;
            double num3 = value1.Z - value2.Z;
            return num * num + num2 * num2 + num3 * num3;
        }
    }
}
