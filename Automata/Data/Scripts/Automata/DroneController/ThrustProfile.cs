using VRageMath;
namespace Automata.DroneController
{
    #region ThrustProfile
    public struct DirectionalValue
    {
        public float Current;
        public float Max;
        public Base6Directions.Direction Direction;

        public DirectionalValue(float currentValue,float maxValue, Base6Directions.Direction direction)
        {
            Current = currentValue;
            Max = maxValue;
            Direction = direction;
        }
    }
    public struct ThrustProfile
    {
        public DirectionalValue Forward;
        public DirectionalValue Backward;
        public DirectionalValue Up;
        public DirectionalValue Down;
        public DirectionalValue Left;
        public DirectionalValue Right;
        public bool Valid;
        public DirectionalValue MaxThrust
        {
            get
            {
                DirectionalValue directionalMax = Forward;

                if (Backward.Max > directionalMax.Max) { directionalMax = Backward; }
                if (Left.Max > directionalMax.Max) { directionalMax = Left; }
                if (Right.Max > directionalMax.Max) { directionalMax = Right; }
                if (Up.Max > directionalMax.Max) { directionalMax = Up; }
                if (Down.Max > directionalMax.Max) { directionalMax = Down; }

                return directionalMax;
            }
        }

        // public DirectionalValue MaxAccel
        // {
        //     get
        //     {
        //         float maxVal = MaxAccelForward;
        //         Base6Directions.Direction maxDir = Base6Directions.Direction.Forward;

        //         if (MaxAccelBackward > maxVal) { maxVal = MaxAccelBackward; maxDir = Base6Directions.Direction.Backward; }
        //         if (MaxAccelLeft > maxVal) { maxVal = MaxAccelLeft; maxDir = Base6Directions.Direction.Left; }
        //         if (MaxAccelRight > maxVal) { maxVal = MaxAccelRight; maxDir = Base6Directions.Direction.Right; }
        //         if (MaxAccelUp > maxVal) { maxVal = MaxAccelUp; maxDir = Base6Directions.Direction.Up; }
        //         if (MaxAccelDown > maxVal) { maxVal = MaxAccelDown; maxDir = Base6Directions.Direction.Down; }

        //         return new DirectionalValue(maxVal, maxDir);
        //     }
        // }
    }
    #endregion
}