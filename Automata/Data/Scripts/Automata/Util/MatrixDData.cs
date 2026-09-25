using ProtoBuf;
using VRageMath;

namespace Automata.Util
{
    [ProtoContract]
    public struct MatrixDData
    {
        [ProtoMember(1)] public double M11;
        [ProtoMember(2)] public double M12;
        [ProtoMember(3)] public double M13;
        [ProtoMember(4)] public double M14;
        [ProtoMember(5)] public double M21;
        [ProtoMember(6)] public double M22;
        [ProtoMember(7)] public double M23;
        [ProtoMember(8)] public double M24;
        [ProtoMember(9)] public double M31;
        [ProtoMember(10)] public double M32;
        [ProtoMember(11)] public double M33;
        [ProtoMember(12)] public double M34;
        [ProtoMember(13)] public double M41;
        [ProtoMember(14)] public double M42;
        [ProtoMember(15)] public double M43;
        [ProtoMember(16)] public double M44;

        // Helper to convert from VRage MatrixD
        public static MatrixDData FromMatrix(MatrixD m)
        {
            return new MatrixDData
            {
                M11 = m.M11,
                M12 = m.M12,
                M13 = m.M13,
                M14 = m.M14,
                M21 = m.M21,
                M22 = m.M22,
                M23 = m.M23,
                M24 = m.M24,
                M31 = m.M31,
                M32 = m.M32,
                M33 = m.M33,
                M34 = m.M34,
                M41 = m.M41,
                M42 = m.M42,
                M43 = m.M43,
                M44 = m.M44
            };
        }

        // Helper to convert back to VRage MatrixD
        public MatrixD ToMatrix()
        {
            return new MatrixD(M11, M12, M13, M14, M21, M22, M23, M24, M31, M32, M33, M34, M41, M42, M43, M44);
        }
    }
}