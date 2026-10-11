using System;

using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using VRage.ModAPI;

using Automata.Util.Logging;

namespace Automata.Util
{
    /// <summary>
    /// One save struct per Automata block (its terminal settings, the messages it sent still in flight, its ongoing
    /// work and operating state), protobuf, base64, prefixed with <see cref="PREFIX"/>, in the block's mod storage
    /// under <see cref="AutomataSession.MOD_GUID"/>. Written whenever the game builds the block's object builder (world
    /// save, streaming, copy: IsSerialized) and when its settings change; runtime parts on the server only. Read once
    /// at load; the runtime parts are applied once item keys are indexed and the message queue is up (<see cref="CanRestore"/>).
    /// Times are stored relative ("ticks ago", "seconds left") since frame counters and clocks don't carry over.
    /// Storage without the prefix is an older format (settings only).
    /// </summary>
    public static class BlockSave
    {
        public const string PREFIX = "AS1:";

        public static bool IsServer { get { return MyAPIGateway.Multiplayer == null || MyAPIGateway.Multiplayer.IsServer; } }

        public static string ToText<T>(T data) where T : class
        {
            return PREFIX + Convert.ToBase64String(MyAPIGateway.Utilities.SerializeToBinary(data));
        }

        /// <summary>The save struct in 'text', or null when 'text' isn't one (older format, empty) or is unreadable.</summary>
        public static T FromText<T>(string text, long entityId) where T : class
        {
            if (string.IsNullOrEmpty(text) || !text.StartsWith(PREFIX, StringComparison.Ordinal)) return null;
            try
            {
                return MyAPIGateway.Utilities.SerializeFromBinary<T>(Convert.FromBase64String(text.Substring(PREFIX.Length)));
            }
            catch (Exception ex)
            {
                Log.Warning("Save {0}: not readable, defaults used ({1})", entityId, ex.Message);
                return null;
            }
        }

        public static string ReadText(IMyEntity entity)
        {
            string text;
            if (entity == null || entity.Storage == null || !entity.Storage.TryGetValue(AutomataSession.MOD_GUID, out text)) return null;
            return text;
        }

        public static void WriteText(IMyEntity entity, string text)
        {
            if (entity == null || text == null) return;
            if (entity.Storage == null) entity.Storage = new MyModStorageComponent();
            entity.Storage.SetValue(AutomataSession.MOD_GUID, text);
        }

        /// <summary>Saved runtime state can be applied: item keys are indexed (inventories name them) and the virtual network is up.</summary>
        public static bool CanRestore
        {
            get { return AutomataSession.componentLookup.Count > 0 && AutomataSession.GetMessageQueue() != null; }
        }

        public static int Now { get { return MyAPIGateway.Session.GameplayFrameCounter; } }

        /// <summary>A frame of the saved session -> ticks before the save.</summary>
        public static int Age(int frame)
        {
            long age = (long)Now - frame;
            return age < 0 ? 0 : age > int.MaxValue / 4 ? int.MaxValue / 4 : (int)age;
        }

        /// <summary>Ticks before the save -> a frame of this session.</summary>
        public static int FrameFromAge(int age)
        {
            return Now - Math.Max(0, age);
        }

        /// <summary>
        /// Runtime state belongs to the block that saved it: a copy (paste, blueprint, projection) has a new entity id and
        /// starts clean (its jobs, auctions and messages are the original's). 0 = saved before the id was recorded.
        /// </summary>
        public static bool OwnState(long savedEntityId, long entityId, string what)
        {
            if (savedEntityId == 0 || savedEntityId == entityId) return true;
            Util.Logging.Log.Info("{0} {1}: saved state of {2} not used (a copy)", what, entityId, savedEntityId);
            return false;
        }

        public static double SecondsLeft(DateTime until)
        {
            return (until - DateTime.UtcNow).TotalSeconds;
        }

        public static DateTime FromSecondsLeft(double seconds)
        {
            return DateTime.UtcNow.AddSeconds(seconds);
        }
    }
}
