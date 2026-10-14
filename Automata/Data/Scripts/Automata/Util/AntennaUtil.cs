using System;

using Sandbox.ModAPI;

namespace Automata.Util
{
    public static class AntennaUtil
    {
        public static bool AntennaReady(IMyRadioAntenna antenna)
        {
            return Alive(antenna) &&
            antenna.IsFunctional &&
            antenna.Enabled &&
            antenna.EnableBroadcasting &&
            antenna.IsWorking;
        }

        /// <summary>Still a block in the world: a removed / ground / pasted-over antenna lingers in caches after it closes.</summary>
        public static bool Alive(IMyRadioAntenna antenna)
        {
            return antenna != null && !antenna.Closed && !antenna.MarkedForClose && antenna.InScene;
        }

        /// <summary>
        /// Broadcast radius, 0 for an antenna that is gone. Never read IMyRadioAntenna.Radius directly: it goes through
        /// the block's radio broadcaster component, which is removed while the block closes (NullReferenceException in
        /// the game's getter - crashed the game from the orchestrator's info text when an antenna was removed).
        /// </summary>
        public static float Range(IMyRadioAntenna antenna)
        {
            if (!Alive(antenna)) return 0f;
            try { return antenna.Radius; }
            catch (NullReferenceException) { return 0f; }
        }
    }
}
