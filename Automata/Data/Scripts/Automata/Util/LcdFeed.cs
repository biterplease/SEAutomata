using System;
using System.Collections.Generic;

using ProtoBuf;
using Sandbox.ModAPI;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Drone;
using Digi.NetworkLib;

namespace Automata.Util
{
    // ------------------------------------------------------------------------------------------------------------
    // LCD feeds. The text of the Automata blocks' LCDs is drawn by a text surface script ("Automata Log", see
    // AutomataLogApp) that runs on each client; the server draws nothing.
    //
    //   client (the app, every ~5 s):  LcdSubscribePacket  "I am showing panel P"
    //   server:  checks the panel (a text panel, content = the app, name contains the tag of an Automata block on its
    //            construct, owner / faction ok), remembers the viewer for KEEPALIVE_TICKS, answers LcdAttachPacket (panel P
    //            shows block B, or why not), and sends B's current feed
    //   server:  while B has viewers, its UpdateLcd builds the header and log lines and sends an LcdFeedPacket to the viewers
    //            when they changed. Without viewers it builds nothing and sends nothing.
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>What an LCD shows: server -> the clients looking at one of the block's panels.</summary>
    [ProtoContract]
    public class LcdFeedPacket : PacketBase
    {
        public LcdFeedPacket() { }

        [ProtoMember(1)] public long OwnerId;          // the Automata block
        [ProtoMember(2)] public int Version;
        [ProtoMember(3)] public uint Foreground;
        [ProtoMember(4)] public uint Background;
        [ProtoMember(5)] public float FontSize;
        [ProtoMember(6)] public List<string> Header;   // null: no header
        [ProtoMember(7)] public bool AlertRow;         // a line reserved under the header for the alert (drones)
        [ProtoMember(8)] public byte AlertLevel;       // 0 none, 1 warning, 2 error
        [ProtoMember(9)] public string Alert;
        [ProtoMember(10)] public List<string> Lines;   // the newest log lines, oldest first

        public override void Received(ref PacketInfo packetInfo, ulong senderSteamId)
        {
            if (MyAPIGateway.Multiplayer.IsServer) return;   // server -> client only (the host draws from the objects directly)
            LcdFeedClient.Apply(this);
        }
    }

    /// <summary>The app asks to be fed, client -> server.</summary>
    [ProtoContract]
    public class LcdSubscribePacket : PacketBase
    {
        public LcdSubscribePacket() { }

        [ProtoMember(1)] public long PanelId;

        public override void Received(ref PacketInfo packetInfo, ulong senderSteamId)
        {
            LcdFeedServer.Subscribe(PanelId, senderSteamId);
        }
    }

    /// <summary>Which block feeds the panel (OwnerId), or why none does (Reason), server -> the viewer.</summary>
    [ProtoContract]
    public class LcdAttachPacket : PacketBase
    {
        public LcdAttachPacket() { }

        public const byte NOT_A_PANEL = 1;
        public const byte NOT_THE_APP = 2;
        public const byte NO_BLOCK = 3;
        public const byte BUSY = 4;

        [ProtoMember(1)] public long PanelId;
        [ProtoMember(2)] public long OwnerId;   // 0: none
        [ProtoMember(3)] public byte Reason;

        public override void Received(ref PacketInfo packetInfo, ulong senderSteamId)
        {
            if (MyAPIGateway.Multiplayer.IsServer) return;
            LcdFeedClient.Apply(this);
        }
    }

    /// <summary>
    /// Server side of one block's LCD feed: who is looking, and the last feed sent. Automata blocks hold one (BlockLcd for
    /// the computers, DroneStatusDisplay for the drones).
    /// </summary>
    public sealed class LcdFeeder
    {
        public const int KEEPALIVE_TICKS = 15 * 60;   // a viewer asks again every ~5 s; unheard for this long it is dropped
        private const int PRUNE_TICKS = 60;
        private const int MAX_VIEWERS = 64;
        private const int MAX_PER_PLAYER = 8;

        private struct Viewer
        {
            public ulong SteamId;
            public long PanelId;
            public int Expires;
        }

        private readonly List<Viewer> viewers = new List<Viewer>();
        private readonly List<ulong> sent = new List<ulong>();
        private IMyTerminalBlock owner;
        private string tag = "";
        private bool ownGridOnly;
        private bool registered;
        private int nextPrune;
        private bool force = true;
        private bool haveKey;
        private ulong lastKey;
        private int version;
        private LcdFeedPacket lastPacket;
        private byte[] lastBytes;

        public IMyTerminalBlock Owner { get { return owner; } }
        /// <summary>The feed's id (packets, client maps): the block's entity id, mixed with the channel for a block's second
        /// feed (the orchestrator's designator LCDs), so both feeds of one block stay apart.</summary>
        public long OwnerId { get { return owner != null ? owner.EntityId ^ feedSalt : 0; } }
        private long feedSalt;
        public string Tag { get { return tag; } }
        public bool OwnGridOnly { get { return ownGridOnly; } }

        /// <summary>The block, its LCD tag and which grids its LCDs may be on (own grid only, or its whole construct); 'channel':
        /// 0 the block's feed, 1.. another feed of the same block. Server only.</summary>
        public void Bind(IMyTerminalBlock block, string lcdTag, bool ownGrid, int channel = 0)
        {
            if (MyAPIGateway.Multiplayer == null || !MyAPIGateway.Multiplayer.IsServer) return;
            owner = block;
            feedSalt = channel == 0 ? 0 : unchecked((long)(0x9E3779B97F4A7C15UL * (ulong)channel));
            tag = lcdTag ?? "";
            ownGridOnly = ownGrid;
            if (!registered)
            {
                registered = true;
                LcdFeedServer.Register(this);
            }
        }

        /// <summary>A client is looking at one of the block's LCDs. Cheap when nobody is.</summary>
        public bool HasViewers
        {
            get
            {
                if (viewers.Count == 0) return false;
                int now = MyAPIGateway.Session.GameplayFrameCounter;
                if (now >= nextPrune)
                {
                    nextPrune = now + PRUNE_TICKS;
                    Prune(now);
                }
                return viewers.Count > 0;
            }
        }

        /// <summary>The next feed must be sent whatever changed (a viewer arrived and nothing is kept for it).</summary>
        public bool ForcePending { get { return force; } }

        /// <summary>True when a feed with this content key must be sent (changed, or forced).</summary>
        public bool Changed(ulong key)
        {
            if (!force && haveKey && key == lastKey) return false;
            force = false;
            haveKey = true;
            lastKey = key;
            return true;
        }

        /// <summary>Sends the feed to every viewer (once serialized).</summary>
        public void Send(LcdFeedPacket packet)
        {
            packet.OwnerId = OwnerId;
            packet.Version = ++version;
            force = false;
            lastPacket = packet;
            lastBytes = null;   // serialized when the first remote viewer needs it
            sent.Clear();
            for (int i = 0; i < viewers.Count; i++)
            {
                if (sent.Contains(viewers[i].SteamId)) continue;
                sent.Add(viewers[i].SteamId);
                DeliverTo(viewers[i].SteamId);
            }
        }

        // The last feed to one player: the host's own client gets the object, the others the (once serialized) bytes
        private void DeliverTo(ulong steamId)
        {
            if (lastPacket == null) return;
            if (!LcdFeedServer.IsLocal(steamId) && lastBytes == null)
                lastBytes = MyAPIGateway.Utilities.SerializeToBinary<PacketBase>(lastPacket);
            LcdFeedServer.DeliverFeed(lastPacket, lastBytes, steamId);
        }

        // Stops feeding a player's panel (the panel now belongs to another block)
        internal void Drop(ulong steamId, long panelId)
        {
            for (int i = viewers.Count - 1; i >= 0; i--)
                if (viewers[i].SteamId == steamId && viewers[i].PanelId == panelId) viewers.RemoveAt(i);
            if (viewers.Count == 0) Reset();
        }

        // The viewer asked (again). False when the block has too many viewers (or this player too many panels).
        internal bool Touch(ulong steamId, long panelId, int now)
        {
            bool knownPlayer = false;
            int ofPlayer = 0;
            for (int i = 0; i < viewers.Count; i++)
            {
                if (viewers[i].SteamId != steamId) continue;
                knownPlayer = true;
                ofPlayer++;
                if (viewers[i].PanelId != panelId) continue;
                var v = viewers[i];
                v.Expires = now + KEEPALIVE_TICKS;
                viewers[i] = v;
                return true;
            }
            if (viewers.Count >= MAX_VIEWERS || ofPlayer >= MAX_PER_PLAYER) return false;
            if (viewers.Count == 0) Reset();   // nothing was kept fresh while nobody watched
            viewers.Add(new Viewer { SteamId = steamId, PanelId = panelId, Expires = now + KEEPALIVE_TICKS });
            nextPrune = Math.Max(nextPrune, now + PRUNE_TICKS);
            if (!knownPlayer) DeliverTo(steamId);   // the last feed, when there is one
            return true;
        }

        // The panel belongs to this block: a text panel named with its tag, on its construct (own grid for drones), owner / faction ok
        internal bool Matches(IMyTextPanel p)
        {
            if (!Quick(p)) return false;
            if (ownGridOnly) return p.CubeGrid == owner.CubeGrid;
            return AnchorDirectory.IsAllowed(owner.OwnerId, p.OwnerId) && LcdFeedServer.SameConstruct(owner.CubeGrid, p.CubeGrid);
        }

        // The cheap part of Matches (every keepalive)
        internal bool Quick(IMyTextPanel p)
        {
            if (owner == null || owner.Closed || owner.MarkedForClose || tag.Length == 0) return false;
            return p.IsFunctional && p.CustomName != null && p.CustomName.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void Prune(int now)
        {
            for (int i = viewers.Count - 1; i >= 0; i--)
            {
                if (viewers[i].Expires >= now) continue;
                LcdFeedServer.Forget(viewers[i].PanelId, this);
                viewers.RemoveAt(i);
            }
            if (viewers.Count == 0) Reset();
        }

        private void Reset()
        {
            force = true;
            haveKey = false;
            lastPacket = null;
            lastBytes = null;
        }

        #region Content keys (no allocation)
        public const ulong Seed = 1469598103934665603UL;

        public static ulong Mix(ulong h, long v)
        {
            return unchecked((h ^ (ulong)v) * 1099511628211UL);
        }

        public static ulong Mix(ulong h, string s)
        {
            if (s == null) return Mix(h, -1);
            for (int i = 0; i < s.Length; i++) h = Mix(h, s[i]);
            return Mix(h, '\n');
        }
        #endregion
    }

    /// <summary>The server's side of the LCD feeds: the feeding blocks, and the clients' requests.</summary>
    public static class LcdFeedServer
    {
        /// <summary>The id of the text surface script (the LCD's content must be set to it).</summary>
        public const string APP = "AutomataLog";
        private const int VERDICT_TICKS = 60 * 60;   // the construct / owner check is repeated this often

        private sealed class Link
        {
            public LcdFeeder Source;
            public int VerifiedAt;
        }

        private static readonly List<LcdFeeder> sources = new List<LcdFeeder>();
        private static readonly Dictionary<long, Link> links = new Dictionary<long, Link>();
        private static readonly List<IMyCubeGrid> groupBuffer = new List<IMyCubeGrid>();

        internal static void Register(LcdFeeder f) { sources.Add(f); }

        internal static void Forget(long panelId, LcdFeeder f)
        {
            Link l;
            if (links.TryGetValue(panelId, out l) && l.Source == f) links.Remove(panelId);
        }

        public static void Clear()
        {
            sources.Clear();
            links.Clear();
            groupBuffer.Clear();
        }

        internal static bool SameConstruct(IMyCubeGrid a, IMyCubeGrid b)
        {
            if (a == b) return true;
            groupBuffer.Clear();
            MyAPIGateway.GridGroups.GetGroup(a, GridLinkTypeEnum.Mechanical, groupBuffer);
            bool same = groupBuffer.Contains(b);
            groupBuffer.Clear();
            return same;
        }

        /// <summary>A client's app asks for panel 'panelId'. Server only.</summary>
        public static void Subscribe(long panelId, ulong steamId)
        {
            if (MyAPIGateway.Multiplayer == null || !MyAPIGateway.Multiplayer.IsServer) return;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (now >= nextCleanup) Cleanup(now);
            IMyEntity e;
            MyAPIGateway.Entities.TryGetEntityById(panelId, out e);
            var panel = e as IMyTextPanel;
            if (panel == null || panel.Closed) { Reply(steamId, panelId, 0, LcdAttachPacket.NOT_A_PANEL); return; }
            if (!NearPanel(steamId, panel)) return;   // the screen only runs near the panel; nobody reads logs from afar
            if (panel.ContentType != ContentType.SCRIPT || !IsApp(panel.Script))
            {
                Reply(steamId, panelId, 0, LcdAttachPacket.NOT_THE_APP);
                return;
            }

            LcdFeeder source = null, previous = null;
            Link link;
            if (links.TryGetValue(panelId, out link))
            {
                previous = link.Source;
                if (link.Source.Quick(panel) && (now - link.VerifiedAt < VERDICT_TICKS || link.Source.Matches(panel)))
                {
                    source = link.Source;
                    if (now - link.VerifiedAt >= VERDICT_TICKS) link.VerifiedAt = now;
                }
                else links.Remove(panelId);
            }
            if (source == null)
            {
                for (int i = sources.Count - 1; i >= 0; i--)
                {
                    var s = sources[i];
                    if (s.Owner == null || s.Owner.Closed || s.Owner.MarkedForClose) { sources.RemoveAt(i); continue; }
                    if (s.Matches(panel) && (source == null || s.OwnerId < source.OwnerId)) source = s;
                }
                if (source != null) links[panelId] = new Link { Source = source, VerifiedAt = now };
            }
            if (previous != null && previous != source) previous.Drop(steamId, panelId);   // the panel moved to another block
            if (source == null) { Reply(steamId, panelId, 0, LcdAttachPacket.NO_BLOCK); return; }
            // (answered every time: a client that lost its mapping gets it back with its next request)
            bool accepted = source.Touch(steamId, panelId, now);
            Reply(steamId, panelId, accepted ? source.OwnerId : 0, accepted ? (byte)0 : LcdAttachPacket.BUSY);
        }

        private static int nextCleanup;
        private static readonly List<long> cleanupBuffer = new List<long>();
        private static readonly List<IMyPlayer> playerBuffer = new List<IMyPlayer>();
        private const double MAX_VIEW_DISTANCE = 1000.0;

        // Closed blocks and deleted panels are forgotten (about once a minute, with a request)
        private static void Cleanup(int now)
        {
            nextCleanup = now + 60 * 60;
            for (int i = sources.Count - 1; i >= 0; i--)
                if (sources[i].Owner == null || sources[i].Owner.Closed || sources[i].Owner.MarkedForClose) sources.RemoveAt(i);
            cleanupBuffer.Clear();
            foreach (var kv in links)
            {
                IMyEntity e;
                if (kv.Value.Source.Owner == null || kv.Value.Source.Owner.Closed || !MyAPIGateway.Entities.TryGetEntityById(kv.Key, out e) || e.Closed)
                    cleanupBuffer.Add(kv.Key);
            }
            for (int i = 0; i < cleanupBuffer.Count; i++) links.Remove(cleanupBuffer[i]);
            cleanupBuffer.Clear();
        }

        // The player's character within reach of the panel (position unknown: allowed)
        private static bool NearPanel(ulong steamId, IMyTextPanel panel)
        {
            playerBuffer.Clear();
            MyAPIGateway.Players.GetPlayers(playerBuffer);
            bool near = true;
            for (int i = 0; i < playerBuffer.Count; i++)
            {
                if (playerBuffer[i].SteamUserId != steamId) continue;
                var character = playerBuffer[i].Character;
                if (character != null)
                    near = Vector3D.DistanceSquared(character.GetPosition(), panel.GetPosition()) <= MAX_VIEW_DISTANCE * MAX_VIEW_DISTANCE;
                break;
            }
            playerBuffer.Clear();
            return near;
        }

        // The script id (the selection may also read as the app's display name)
        private static bool IsApp(string script)
        {
            return string.Equals(script, APP, StringComparison.OrdinalIgnoreCase) || string.Equals(script, "Automata Log", StringComparison.OrdinalIgnoreCase);
        }

        private static void Reply(ulong steamId, long panelId, long ownerId, byte reason)
        {
            var a = new LcdAttachPacket { PanelId = panelId, OwnerId = ownerId, Reason = reason };
            if (IsLocal(steamId)) LcdFeedClient.Apply(a);
            else if (AutomataSession.Instance != null && AutomataSession.Instance.Net != null) AutomataSession.Instance.Net.SendToPlayer(a, steamId);
        }

        internal static void DeliverFeed(LcdFeedPacket packet, byte[] bytes, ulong steamId)   // 'bytes': null for the local player
        {
            if (IsLocal(steamId)) LcdFeedClient.Apply(packet);   // the host (and single player) draw from the same object
            else if (AutomataSession.Instance != null && AutomataSession.Instance.Net != null) AutomataSession.Instance.Net.SendToPlayer(packet, steamId, bytes);
        }

        internal static bool IsLocal(ulong steamId)
        {
            return steamId == MyAPIGateway.Multiplayer.MyId;
        }
    }

    /// <summary>The client's side: what the server last said about each panel and block. Read by the app.</summary>
    public static class LcdFeedClient
    {
        private static readonly Dictionary<long, long> panelOwner = new Dictionary<long, long>();
        private static readonly Dictionary<long, byte> panelReason = new Dictionary<long, byte>();
        private static readonly Dictionary<long, LcdFeedPacket> feeds = new Dictionary<long, LcdFeedPacket>();

        public static void Apply(LcdFeedPacket p)
        {
            feeds[p.OwnerId] = p;
        }

        public static void Apply(LcdAttachPacket a)
        {
            if (a.OwnerId != 0)
            {
                panelOwner[a.PanelId] = a.OwnerId;
                panelReason.Remove(a.PanelId);
            }
            else
            {
                panelOwner.Remove(a.PanelId);
                panelReason[a.PanelId] = a.Reason;
            }
        }

        /// <summary>The feed of the block showing on this panel (null: none yet, 'reason' says why, 0 = no answer yet).</summary>
        public static LcdFeedPacket Get(long panelId, out byte reason)
        {
            reason = 0;
            long owner;
            LcdFeedPacket feed;
            if (panelOwner.TryGetValue(panelId, out owner) && feeds.TryGetValue(owner, out feed)) return feed;
            panelReason.TryGetValue(panelId, out reason);
            return null;
        }

        /// <summary>The app is showing this panel: the server feeds it (and keeps feeding while this repeats).</summary>
        public static void Request(long panelId)
        {
            var session = AutomataSession.Instance;
            if (session == null || session.Net == null) return;
            session.Net.SendToServer(new LcdSubscribePacket { PanelId = panelId });
        }

        public static void Clear()
        {
            panelOwner.Clear();
            panelReason.Clear();
            feeds.Clear();
        }
    }
}
