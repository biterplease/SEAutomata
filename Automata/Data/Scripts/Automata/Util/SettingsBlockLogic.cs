using System;

using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.ModAPI;
using VRage.ObjectBuilders;

using Automata.Network;
using Automata.Util.Logging;
using Digi.NetworkLib;

namespace Automata.Util
{
    /// <summary>
    /// Game logic of an Automata block with player settings. The block's save struct (<see cref="BlockSave"/>:
    /// settings + runtime state) lives in its mod storage. A terminal change is applied locally, saved, and its
    /// settings sent whole (XML) to the server (terminal access checked there), which relays them to the other clients:
    /// every machine holds the same settings, and the server's storage is what gets saved / streamed.
    /// </summary>
    public abstract class SettingsBlockLogic : MyGameLogicComponent
    {
        protected IMyTerminalBlock block;

        /// <summary>Current settings as XML (network).</summary>
        protected abstract string SettingsXml();

        /// <summary>Replace the settings with these (network, or storage of the older format); false when unreadable.</summary>
        protected abstract bool LoadSettingsXml(string xml);

        /// <summary>The whole save struct (<see cref="BlockSave.ToText"/>): settings, and on the server the runtime state.</summary>
        protected abstract string SaveText();

        /// <summary>Takes the save struct apart (settings now, runtime state kept for later); false when 'text' isn't one.</summary>
        protected abstract bool LoadSaveText(string text);

        /// <summary>Settings were replaced or edited: re-read what depends on them.</summary>
        protected abstract void OnSettingsChanged();

        protected static bool IsServer { get { return MyAPIGateway.Multiplayer == null || MyAPIGateway.Multiplayer.IsServer; } }

        /// <summary>Reads the save struct, or settings of the older format (false: none / unreadable, defaults kept).</summary>
        protected bool LoadFromStorage()
        {
            storageRead = true;
            try
            {
                string text = BlockSave.ReadText(Entity);
                if (string.IsNullOrEmpty(text)) return false;
                if (LoadSaveText(text)) return true;
                if (text.TrimStart().StartsWith("<", StringComparison.Ordinal)) return LoadSettingsXml(text);   // settings XML (older saves)
            }
            catch (Exception ex)
            {
                Log.Warning("{0} {1}: settings not readable, defaults used ({2})", GetType().Name, Entity.EntityId, ex.Message);
            }
            return false;
        }

        // Set once the storage was read: before that (no physics yet, concealed grid...) saving would write defaults over it
        private bool storageRead;

        private void WriteStorage()
        {
            if (!storageRead) return;
            BlockSave.WriteText(Entity, SaveText());
        }

        /// <summary>A terminal edit: save, re-read, share with the server / other players.</summary>
        protected void SettingsEdited()
        {
            WriteStorage();
            OnSettingsChanged();
            if (block != null) block.RefreshCustomInfo();
            if (MyAPIGateway.Multiplayer == null || !MyAPIGateway.Multiplayer.MultiplayerActive) return;
            var net = AutomataSession.Instance != null ? AutomataSession.Instance.Net : null;
            if (net == null) return;
            var packet = new BlockSettingsPacket { EntityId = Entity.EntityId, Xml = SettingsXml() };
            if (IsServer) net.SendToEveryone(packet);
            else net.SendToServer(packet);
        }

        /// <summary>Settings from the network (see <see cref="BlockSettingsPacket"/>).</summary>
        public void ReceiveSettings(BlockSettingsPacket p, ref PacketInfo info, ulong senderSteamId)
        {
            if (IsServer)
            {
                if (senderSteamId == MyAPIGateway.Multiplayer.MyId) return;   // the host's own edit, applied already
                long identity = MyAPIGateway.Players.TryGetIdentityId(senderSteamId);
                if (identity == 0 || block == null || !block.HasPlayerAccess(identity))
                {
                    Log.Warning("Net: {0} {1}: settings from {2} rejected (no terminal access)", GetType().Name, Entity.EntityId, senderSteamId);
                    return;
                }
                info.Relay = RelayMode.ToOthers;
            }
            try
            {
                if (!LoadSettingsXml(p.Xml))
                {
                    Log.Warning("Net: {0} {1}: settings from {2} not readable", GetType().Name, Entity.EntityId, senderSteamId);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Net: {0} {1}: settings from {2} not readable ({3})", GetType().Name, Entity.EntityId, senderSteamId, ex.Message);
                return;
            }
            Log.Debug("Net: {0} {1}: settings received from {2}", GetType().Name, Entity.EntityId, senderSteamId);
            WriteStorage();
            OnSettingsChanged();
            if (block != null) block.RefreshCustomInfo();
        }

        public override bool IsSerialized()
        {
            try { WriteStorage(); }
            catch (Exception ex) { Log.Error("{0} {1}: settings not saved: {2}", GetType().Name, Entity.EntityId, ex.Message); }
            return base.IsSerialized();
        }
    }
}
