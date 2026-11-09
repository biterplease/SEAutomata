using System;
using System.Collections.Generic;

using ProtoBuf;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

using Automata.Util;
using Automata.Util.Logging;
using Automata.Util.Strings;

namespace Automata.Logistics
{
    /// <summary>
    /// Conveyor networks by a fixed index, their own Min / Max lists, and their outlines (2026-11-08).
    ///  - Index ("CN 3"): AssignNetworkIndexes after every MapNetworks. A network keeps the index of the one it shares the
    ///    most blocks with (this session), else the one recorded for its key (its smallest block id: cnKeys, saved with
    ///    the state - server only, so a client's settings never overwrite it), else the one whose anchor cell it holds
    ///    (settings.NetworkAnchors: a block's cell on this computer's grid - a pasted copy of the grid has new block ids
    ///    and none of the state, but the same cells: its networks get the original's indexes and its lists land on the
    ///    same networks), else a new one past every index known. Indexes are never reused.
    ///  - Lists: the "Any" list (settings.Quotas) is the main network's, as before; another network may have a list of its
    ///    own (settings.NetworkQuotas, by index), requested / pushed at its own connectors. A network that is the main one
    ///    now uses the "Any" list. The terminal edits the list of the network picked in its dropdown (not saved).
    ///  - Clients: the dropdown and the outlines need the server's map: LcNetworksRequestPacket / LcNetworksPacket (single
    ///    player / host: read here).
    ///  - Outlines: "Identify CN" outlines the picked network's blocks in green (picking another moves them), "/automata
    ///    debugdraw cn" every network of the computers in range; off after DebugDrawTimeoutSeconds.
    /// </summary>
    public partial class LogisticsComputerBlock : IDebugDrawable
    {
        // ---- Indexes (server) ----
        private readonly List<long> cnKeys = new List<long>();                       // index - 1 -> key
        private readonly List<int> networkIndex = new List<int>();                   // parallel to networks
        private readonly Dictionary<int, HashSet<long>> indexMembers = new Dictionary<int, HashSet<long>>();
        private readonly List<IMyCubeBlock> cnBlocks = new List<IMyCubeBlock>();

        private void AssignNetworkIndexes()
        {
            networkIndex.Clear();
            var used = new HashSet<int>();
            var members = new Dictionary<int, HashSet<long>>();
            var anchors = new List<NetworkAnchor>();
            var lcGrid = block != null ? block.CubeGrid : null;
            for (int i = 0; i < networks.Count; i++)
            {
                cnBlocks.Clear();
                networks[i].Blocks(cnBlocks);
                var ids = new HashSet<long>();
                // Its blocks' cells: on this computer's grid its own cell, on another grid (a subgrid, a docked ship) the cell
                // its centre falls in on this grid - both kept by a copy of the grid, unlike the entity ids
                var cells = new HashSet<Vector3I>();
                var subCells = new HashSet<Vector3I>();
                bool anchorSet = false, anchorSub = true;
                Vector3I anchor = Vector3I.Zero;
                for (int b = 0; b < cnBlocks.Count; b++)
                {
                    var blk = cnBlocks[b];
                    if (blk == null) continue;
                    ids.Add(blk.EntityId);
                    if (lcGrid == null) continue;
                    bool sub = blk.CubeGrid != lcGrid;
                    Vector3I cell = sub ? lcGrid.WorldToGridInteger(blk.GetPosition()) : blk.Position;
                    (sub ? subCells : cells).Add(cell);
                    // The anchor: the lowest cell on this grid, else the lowest of the others
                    if (!anchorSet || (anchorSub && !sub) || (anchorSub == sub && Lower(cell, anchor))) { anchor = cell; anchorSub = sub; anchorSet = true; }
                }
                cnBlocks.Clear();
                int best = 0, bestShared = 0;
                foreach (var kv in indexMembers)
                {
                    if (used.Contains(kv.Key)) continue;
                    int shared = 0;
                    foreach (var id in ids) if (kv.Value.Contains(id)) shared++;
                    if (shared > bestShared) { bestShared = shared; best = kv.Key; }
                }
                long key = networkKeys[i];
                if (best == 0)
                {
                    int at = cnKeys.IndexOf(key);
                    if (at >= 0 && !used.Contains(at + 1)) best = at + 1;
                }
                if (best == 0) best = AnchoredIndex(cells, subCells, used);   // a copy of the grid: its anchors came with the settings
                if (best == 0) best = NextFreeIndex(used);
                while (cnKeys.Count < best) cnKeys.Add(0);
                cnKeys[best - 1] = key;   // (after a load it is found by this key)
                used.Add(best);
                networkIndex.Add(best);
                members[best] = ids;
                if (anchorSet) anchors.Add(new NetworkAnchor { Index = best, Cell = Vector3IData.FromVector3I(anchor), OnSubgrid = anchorSub });
                networks[i].Nickname = Loc.F(MyStrings.Logistics_Network, best);
            }
            bool anchorsChanged = SaveAnchors(anchors);
            CarryNetworkLists(indexMembers, members, anchorsChanged);
            indexMembers.Clear();
            foreach (var kv in members) indexMembers[kv.Key] = kv.Value;
            cnInfoFrame = int.MinValue / 2;   // the dropdown / outlines read it again
        }

        // ---- Joins and splits (server) ----
        private int cnMainBefore;   // the main network's index before the re-map (its list: the "Any" one)

        /// <summary>
        /// After a re-map (2026-11-08): networks joined by conveyors (shared blocks with two or more of before) take one list
        /// (MergeQuotaLists); a network split apart (a conveyor, connecting block, hinge, piston or rotor gone) leaves every
        /// part a copy of its list. A resulting network with no connector or no cargo container can't take a request: its
        /// list is cleared. The main network's list is the "Any" one: it follows the part of it with the most cargo
        /// containers (that part, and every other, also gets the list as its own: whichever is main next, nothing is lost).
        /// Lists of networks gone (joined into another) are dropped. Nothing before (the first map, a load): nothing done.
        /// </summary>
        private void CarryNetworkLists(Dictionary<int, HashSet<long>> before, Dictionary<int, HashSet<long>> after, bool anchorsChanged)
        {
            if (settings == null) return;
            if (before.Count == 0)
            {
                if (anchorsChanged) SettingsEdited();
                return;
            }
            var oldLists = new Dictionary<int, List<string>>();
            foreach (var o in before.Keys) oldLists[o] = new List<string>(ListOfIndex(o, o == cnMainBefore));
            var parts = new Dictionary<int, int>();   // old index -> how many networks share blocks with it now
            var sourcesOf = new Dictionary<int, List<int>>();
            for (int i = 0; i < networks.Count && i < networkIndex.Count; i++)
            {
                HashSet<long> ids;
                if (!after.TryGetValue(networkIndex[i], out ids)) continue;
                var sources = new List<int>();
                foreach (var kv in before)
                {
                    foreach (var id in ids) if (kv.Value.Contains(id)) { sources.Add(kv.Key); break; }
                }
                sourcesOf[networkIndex[i]] = sources;
                for (int s = 0; s < sources.Count; s++) { int n; parts.TryGetValue(sources[s], out n); parts[sources[s]] = n + 1; }
            }
            bool changed = false;
            List<string> anyList = null;
            int anyCargo = -1;
            for (int i = 0; i < networks.Count && i < networkIndex.Count; i++)
            {
                int idx = networkIndex[i];
                List<int> sources;
                if (!sourcesOf.TryGetValue(idx, out sources) || sources.Count == 0) continue;   // all new blocks
                bool joined = sources.Count > 1;
                int p;
                bool split = !joined && parts.TryGetValue(sources[0], out p) && p > 1;
                if (!joined && !split && sources[0] == idx) continue;   // as it was
                var lists = new List<List<string>>();
                for (int s = 0; s < sources.Count; s++) lists.Add(oldLists[sources[s]]);
                var result = MergeQuotaLists(lists);
                string name = networks[i].Nickname;
                if (result.Count > 0 && (networks[i].connectors.Count == 0 || networks[i].cargoContainers.Count == 0))
                {
                    result.Clear();
                    log.Add(Loc.F(MyStrings.Logistics_CnListCleared, name));
                }
                else if (joined) log.Add(Loc.F(MyStrings.Logistics_CnJoined, NamesOf(sources), name, result.Count));
                else if (sources[0] != idx || result.Count > 0) log.Add(Loc.F(MyStrings.Logistics_CnSplit, Loc.F(MyStrings.Logistics_Network, sources[0]), name, result.Count));
                if (Log.DebugEnabled) Log.Debug("LC {0}: {1} {2} from {3}: {4} Min / Max line(s)", entityId, name, joined ? "joined" : "split", NamesOf(sources), result.Count);
                changed |= SetOwnList(idx, result);
                if (sources.Contains(cnMainBefore) && networks[i].cargoContainers.Count > anyCargo)
                {
                    anyCargo = networks[i].cargoContainers.Count;
                    anyList = result;
                }
            }
            if (anyList != null && !SameLines(anyList, settings.Quotas))
            {
                settings.Quotas.Clear();
                settings.Quotas.AddRange(anyList);
                changed = true;
            }
            // Joined into another: its own list goes
            if (settings.NetworkQuotas != null)
                changed |= settings.NetworkQuotas.RemoveAll(q => q != null && before.ContainsKey(q.Index) && !after.ContainsKey(q.Index)) > 0;
            if (!changed)
            {
                if (anchorsChanged) SettingsEdited();
                return;
            }
            quotasDirty = true;
            SettingsEdited();
        }

        // A network's list as it was: the "Any" one for the main network, else its own (empty: none)
        private List<string> ListOfIndex(int index, bool main)
        {
            if (main) return settings.Quotas;
            if (settings.NetworkQuotas != null)
                for (int i = 0; i < settings.NetworkQuotas.Count; i++)
                    if (settings.NetworkQuotas[i] != null && settings.NetworkQuotas[i].Index == index && settings.NetworkQuotas[i].Quotas != null)
                        return settings.NetworkQuotas[i].Quotas;
            noQuotas.Clear();
            return noQuotas;
        }

        // A network's own list set (empty: dropped); true when it changed
        private bool SetOwnList(int index, List<string> lines)
        {
            if (settings.NetworkQuotas == null) settings.NetworkQuotas = new List<NetworkQuotaList>();
            for (int i = 0; i < settings.NetworkQuotas.Count; i++)
            {
                var q = settings.NetworkQuotas[i];
                if (q == null || q.Index != index) continue;
                if (lines.Count == 0) { settings.NetworkQuotas.RemoveAt(i); return true; }
                if (SameLines(q.Quotas, lines)) return false;
                q.Quotas = new List<string>(lines);
                return true;
            }
            if (lines.Count == 0) return false;
            settings.NetworkQuotas.Add(new NetworkQuotaList { Index = index, Quotas = new List<string>(lines) });
            return true;
        }

        /// <summary>
        /// Lists joined into one: per item the one line with the larger Min wins, the larger Max only on equal Mins (no
        /// mixing: "500:1000" and "400:1100" give "500:1000"; "500:1000" and "501:1001" give "501:1001"). Lines that don't
        /// parse are kept once.
        /// </summary>
        public static List<string> MergeQuotaLists(List<List<string>> lists)
        {
            var order = new List<MyStringHash>();
            var best = new Dictionary<MyStringHash, QuotaEntry>(MyStringHash.Comparer);
            var raw = new List<string>();
            for (int l = 0; l < lists.Count; l++)
                for (int i = 0; i < lists[l].Count; i++)
                {
                    QuotaEntry e;
                    if (!QuotaEntry.TryParse(lists[l][i], out e)) { if (!raw.Contains(lists[l][i])) raw.Add(lists[l][i]); continue; }
                    QuotaEntry had;
                    if (!best.TryGetValue(e.Key, out had)) { best[e.Key] = e; order.Add(e.Key); continue; }
                    if (e.Min > had.Min || (e.Min == had.Min && e.Max > had.Max)) best[e.Key] = e;
                }
            var result = new List<string>(order.Count + raw.Count);
            for (int i = 0; i < order.Count; i++) result.Add(best[order[i]].ToString());
            result.AddRange(raw);
            return result;
        }

        private static bool SameLines(List<string> a, List<string> b)
        {
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static string NamesOf(List<int> indexes)
        {
            var names = new List<string>(indexes.Count);
            for (int i = 0; i < indexes.Count; i++) names.Add(Loc.F(MyStrings.Logistics_Network, indexes[i]));
            return string.Join(", ", names);
        }

        private static bool Lower(Vector3I a, Vector3I b)
        {
            return a.X != b.X ? a.X < b.X : a.Y != b.Y ? a.Y < b.Y : a.Z < b.Z;
        }

        // The index whose recorded anchor cell (settings.NetworkAnchors) is one of this network's cells, not taken yet; 0: none
        private int AnchoredIndex(HashSet<Vector3I> cells, HashSet<Vector3I> subCells, HashSet<int> used)
        {
            if (settings == null || settings.NetworkAnchors == null) return 0;
            for (int i = 0; i < settings.NetworkAnchors.Count; i++)
            {
                var a = settings.NetworkAnchors[i];
                if (a == null || a.Index <= 0 || used.Contains(a.Index)) continue;
                if ((a.OnSubgrid ? subCells : cells).Contains(a.Cell.ToVector3I())) return a.Index;
            }
            return 0;
        }

        // A new index: past every one this computer knows (its keys, its anchors, its lists' labels: a list never lands on
        // a network that isn't the one it was made for)
        private int NextFreeIndex(HashSet<int> used)
        {
            int max = cnKeys.Count;
            if (settings != null && settings.NetworkAnchors != null)
                for (int i = 0; i < settings.NetworkAnchors.Count; i++) if (settings.NetworkAnchors[i] != null) max = Math.Max(max, settings.NetworkAnchors[i].Index);
            if (settings != null && settings.NetworkQuotas != null)
                for (int i = 0; i < settings.NetworkQuotas.Count; i++) if (settings.NetworkQuotas[i] != null) max = Math.Max(max, settings.NetworkQuotas[i].Index);
            foreach (var u in used) max = Math.Max(max, u);
            return max + 1;
        }

        // The anchors into the settings (copied with the grid; synced): true when they changed
        private bool SaveAnchors(List<NetworkAnchor> anchors)
        {
            if (settings == null) return false;
            if (settings.NetworkAnchors == null) settings.NetworkAnchors = new List<NetworkAnchor>();
            var old = settings.NetworkAnchors;
            bool same = old.Count == anchors.Count;
            for (int i = 0; i < anchors.Count && same; i++)
                same = old[i] != null && old[i].Index == anchors[i].Index && old[i].OnSubgrid == anchors[i].OnSubgrid
                       && old[i].Cell.ToVector3I() == anchors[i].Cell.ToVector3I();
            if (same) return false;
            settings.NetworkAnchors = anchors;
            return true;
        }

        private ConveyorNetwork NetworkByIndex(int index)
        {
            for (int i = 0; i < networks.Count && i < networkIndex.Count; i++) if (networkIndex[i] == index) return networks[i];
            return null;
        }

        private int IndexOfNetwork(ConveyorNetwork network)
        {
            int i = networks.IndexOf(network);
            return i >= 0 && i < networkIndex.Count ? networkIndex[i] : 0;
        }

        // ---- Save / load (server) ----
        private readonly Dictionary<int, NetworkJobsState> pendingNetJobs = new Dictionary<int, NetworkJobsState>();

        private void RestoreNetworks(LogisticsComputerState s)
        {
            cnKeys.Clear();
            if (s.NetworkKeys != null) cnKeys.AddRange(s.NetworkKeys);
            pendingNetJobs.Clear();
            if (s.NetworkJobs == null) return;
            for (int i = 0; i < s.NetworkJobs.Count; i++)
            {
                var nj = s.NetworkJobs[i];
                if (nj == null || nj.Index <= 0) continue;
                QuotaSet set;
                if (netSets.TryGetValue(nj.Index, out set)) ApplyJobs(set, nj);
                else pendingNetJobs[nj.Index] = nj;
            }
        }

        // A network's list parsed for the first time: its jobs from the saved state, if any
        private void RestorePendingJobs(QuotaSet set)
        {
            NetworkJobsState nj;
            if (!pendingNetJobs.TryGetValue(set.Index, out nj)) return;
            pendingNetJobs.Remove(set.Index);
            ApplyJobs(set, nj);
        }

        private static void ApplyJobs(QuotaSet set, NetworkJobsState nj)
        {
            set.Request = RestoreJob(nj.Request);
            set.Push = RestoreJob(nj.Push);
            if (set.Request != null) set.Request.Network = set.Index;
            if (set.Push != null) set.Push.Network = set.Index;
        }

        // ---- The map for the terminal and the outlines ----
        private List<ConveyorNetworkInfo> cnInfo = new List<ConveyorNetworkInfo>();
        private int cnInfoFrame = int.MinValue / 2;
        private int cnAskedFrame = int.MinValue / 2;
        private const int CN_INFO_TICKS = 120;   // the map read / asked for again at most this often

        /// <summary>Server: every network, by index: its label (its cargo containers' volume), main or not, its blocks.</summary>
        public List<ConveyorNetworkInfo> BuildNetworkInfo()
        {
            var list = new List<ConveyorNetworkInfo>();
            for (int i = 0; i < networks.Count && i < networkIndex.Count; i++)
            {
                var n = networks[i];
                cnBlocks.Clear();
                n.Blocks(cnBlocks);
                var info = new ConveyorNetworkInfo { Index = networkIndex[i], Main = n == primary, Label = NetworkLabel(n, cnBlocks.Count) };
                for (int b = 0; b < cnBlocks.Count; b++) if (cnBlocks[b] != null) info.Blocks.Add(cnBlocks[b].EntityId);
                cnBlocks.Clear();
                list.Add(info);
            }
            list.Sort((a, b) => a.Index.CompareTo(b.Index));
            return list;
        }

        // "15.6 m^3": the volume of its (functional) cargo containers
        private static string NetworkLabel(ConveyorNetwork n, int blocks)
        {
            return ((double)n.MaxVolume).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " m³";
        }

        // The map as this machine knows it: read here (server), else the last one the server sent (asked for again now and then)
        private List<ConveyorNetworkInfo> NetworkInfo()
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (now - cnInfoFrame < CN_INFO_TICKS) return cnInfo;
            if (IsServer)
            {
                cnInfo = BuildNetworkInfo();
                cnInfoFrame = now;
            }
            else if (now - cnAskedFrame >= CN_INFO_TICKS && AutomataSession.Instance != null && AutomataSession.Instance.Net != null)
            {
                cnAskedFrame = now;
                AutomataSession.Instance.Net.SendToServer(new Network.LcNetworksRequestPacket { LcId = entityId });
            }
            return cnInfo;
        }

        /// <summary>Client: the server's map arrived.</summary>
        public void ReceiveNetworkInfo(List<ConveyorNetworkInfo> list)
        {
            cnInfo = list ?? new List<ConveyorNetworkInfo>();
            cnInfoFrame = MyAPIGateway.Session.GameplayFrameCounter;
            LogisticsComputerTerminalControls.RefreshNetworks();
        }

        // ---- Terminal: the dropdown (this player's, not saved) ----
        /// <summary>The picked network: 0 "Any", else a CN index.</summary>
        public long Terminal_SelectedNetwork;

        public void Terminal_NetworkItems(List<MyTerminalControlComboBoxItem> items)
        {
            items.Add(new MyTerminalControlComboBoxItem { Key = 0, Value = MyStrings.Logistics_Any });
            var list = NetworkInfo();
            for (int i = 0; i < list.Count; i++)
            {
                var n = list[i];
                string text = Loc.F(MyStrings.Logistics_Network, n.Index) + ": " + n.Label + (n.Main ? " [M]" : "");   // "CN 2: 15.6 m^3 [M]"
                items.Add(new MyTerminalControlComboBoxItem { Key = n.Index, Value = MyStringId.GetOrCompute(text) });
            }
        }

        public void Terminal_SelectNetwork(long key)
        {
            if (key == Terminal_SelectedNetwork) return;
            Terminal_SelectedNetwork = key;
            Terminal_SelectedQuota = -1;
            Terminal_SelectedItem = MyStringHash.NullOrEmpty;
            Terminal_EditText.Clear();
            if (cnDrawIndex != -1 && cnDrawIndex != 0) cnDrawIndex = key > 0 ? (int)key : -2;   // outlining one: the picked one now
        }

        // The picked network is the main one (or "Any"): the "Any" list
        private bool ScopeIsMain
        {
            get
            {
                if (Terminal_SelectedNetwork <= 0) return true;
                var list = NetworkInfo();
                for (int i = 0; i < list.Count; i++) if (list[i].Index == Terminal_SelectedNetwork) return list[i].Main;
                return false;
            }
        }

        private static readonly List<string> noQuotas = new List<string>();

        // The list the terminal shows / edits: "Any" (the main network's), else the picked network's own ('create': made
        // when it has none yet; not: an empty one)
        private List<string> ScopeList(bool create)
        {
            if (ScopeIsMain) return settings.Quotas;
            int idx = (int)Terminal_SelectedNetwork;
            if (settings.NetworkQuotas == null) settings.NetworkQuotas = new List<NetworkQuotaList>();
            for (int i = 0; i < settings.NetworkQuotas.Count; i++)
                if (settings.NetworkQuotas[i] != null && settings.NetworkQuotas[i].Index == idx)
                {
                    if (settings.NetworkQuotas[i].Quotas == null) settings.NetworkQuotas[i].Quotas = new List<string>();
                    return settings.NetworkQuotas[i].Quotas;
                }
            if (!create) { noQuotas.Clear(); return noQuotas; }
            var nq = new NetworkQuotaList { Index = idx };
            settings.NetworkQuotas.Add(nq);
            return nq.Quotas;
        }

        private void DropEmptyNetworkLists()
        {
            if (settings.NetworkQuotas != null) settings.NetworkQuotas.RemoveAll(q => q == null || q.Quotas == null || q.Quotas.Count == 0);
        }

        // ---- Outlines ----
        private int cnDrawIndex = -1;   // -1 off; 0 every network; -2 the main one; else that CN index
        private int cnDrawStart;
        private static readonly MyStringId CnDrawMaterial = MyStringId.GetOrCompute("Square");
        private static readonly Color CnColor = Color.Lime;

        /// <summary>Terminal "Identify CN": the picked network outlined ("Any": the main one).</summary>
        public void Terminal_IdentifyNetwork()
        {
            StartCnDraw(Terminal_SelectedNetwork > 0 ? (int)Terminal_SelectedNetwork : -2);
        }

        /// <summary>"/automata debugdraw cn": every network of this computer outlined.</summary>
        public void DrawAllNetworks()
        {
            StartCnDraw(0);
        }

        private void StartCnDraw(int which)
        {
            cnDrawIndex = which;
            cnDrawStart = MyAPIGateway.Session.GameplayFrameCounter;
            cnInfoFrame = int.MinValue / 2;
            cnAskedFrame = int.MinValue / 2;
            NetworkInfo();
            Drone.DroneControllerBlock.RequestClientSettings();   // multiplayer clients: the server's draw timeout
            if (AutomataSession.Instance != null) AutomataSession.Instance.RequestDraw(this, true);
        }

        public bool DrawDebug()
        {
            if (cnDrawIndex == -1 || block == null || block.MarkedForClose) return false;
            int timeout = Config.ServerConfig.Instance.Drone.DebugDrawTimeoutSeconds * 60;
            if (MyAPIGateway.Session.GameplayFrameCounter - cnDrawStart > timeout) { cnDrawIndex = -1; return false; }
            var list = NetworkInfo();
            for (int i = 0; i < list.Count; i++)
            {
                var n = list[i];
                if (cnDrawIndex != 0 && !(cnDrawIndex == -2 ? n.Main : n.Index == cnDrawIndex)) continue;
                for (int b = 0; b < n.Blocks.Count; b++)
                {
                    IMyEntity e;
                    if (!MyAPIGateway.Entities.TryGetEntityById(n.Blocks[b], out e) || e == null || e.MarkedForClose) continue;
                    MatrixD m = e.WorldMatrix;
                    BoundingBoxD box = e.LocalAABB;
                    box.Inflate(0.05);
                    Color c = CnColor;
                    MySimpleObjectDraw.DrawTransparentBox(ref m, ref box, ref c, ref c, MySimpleObjectRasterizer.Wireframe, Vector3I.One, 0.03f,
                        null, CnDrawMaterial, false, -1, VRageRender.MyBillboard.BlendTypeEnum.SDR);
                }
            }
            return true;
        }

        public void StopDebugDraw()
        {
            cnDrawIndex = -1;
        }
    }

    /// <summary>A conveyor network as the terminal and the outlines see it (server -> client: LcNetworksPacket).</summary>
    [ProtoContract]
    public class ConveyorNetworkInfo
    {
        [ProtoMember(1)] public int Index;
        [ProtoMember(2)] public string Label;
        [ProtoMember(3)] public bool Main;
        [ProtoMember(4)] public List<long> Blocks = new List<long>();
    }
}
