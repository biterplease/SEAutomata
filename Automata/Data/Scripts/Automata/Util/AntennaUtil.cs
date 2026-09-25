using Sandbox.ModAPI;

namespace Automata.Util
{
    public static class AntennaUtil
    {
        public static bool AntennaReady(IMyRadioAntenna antenna)
        {
            return antenna != null &&
            antenna.IsFunctional &&
            antenna.Enabled &&
            antenna.EnableBroadcasting &&
            antenna.IsWorking;
        }
    }
}