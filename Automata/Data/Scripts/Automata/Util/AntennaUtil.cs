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

        
        public static bool AntennaReady(IMyLaserAntenna antenna)
        {
            return Alive(antenna) &&
            antenna.IsFunctional &&
            antenna.Enabled &&
            antenna.IsWorking &&
            antenna.Status == Sandbox.ModAPI.Ingame.MyLaserAntennaStatus.Connected;
        }

        /// <summary>Still a block in the world: a removed / ground / pasted-over antenna lingers in caches after it closes.</summary>
        public static bool Alive(IMyRadioAntenna antenna)
        {
            return antenna != null && !antenna.Closed && !antenna.MarkedForClose && antenna.InScene;
        }

                /// <summary>Still a block in the world: a removed / ground / pasted-over antenna lingers in caches after it closes.</summary>
        public static bool Alive(IMyLaserAntenna antenna)
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

        public static float Range(IMyLaserAntenna antenna)
        {
            if (!Alive(antenna)) return 0f;
            try {return antenna.Range; }
            catch (NullReferenceException) {return 0f; }
        }
        public static string Name(IMyRadioAntenna antenna)
        {
            try {return antenna.CustomName; }
            catch (NullReferenceException) {return "Antenna"; }
        }
        public static string Name(IMyLaserAntenna antenna)
        {
            try {return antenna.CustomName; }
            catch (NullReferenceException) {return "Laser Antenna"; }
        }
    }
}
