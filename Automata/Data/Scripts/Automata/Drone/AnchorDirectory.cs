using System;
using System.Collections.Generic;

using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace Automata.Drone
{
    public enum AnchorKind : byte
    {
        Connector = 0,
        Beacon = 1,
    }

    public struct AnchorEntry
    {
        public long EntityId;
        public string Name;
        public double Distance;     // from the drone at scan time
        public AnchorKind Kind;
        public long OwnerId;        // block owner at scan time
    }

    /// <summary>
    /// Session-owned directory of connectors and beacons a drone may use as home / navigation anchor.
    ///
    /// SECURITY: only blocks owned by the drone's owner, or by a member of the owner's faction, are ever
    /// returned. Anything else would let players locate enemy bases through the terminal. The same rule is
    /// applied again whenever a stored id is resolved (<see cref="Resolve"/>), since ownership can change.
    ///
    /// Cost control, per drone:
    /// - automatic list: ConnectorScanRadius around the drone, checked every ConnectorScanIntervalSeconds but only
    ///   rescanned when the drone moved ConnectorScanMinMoveMeters or the list is ConnectorScanMaxAgeSeconds old;
    /// - "Scan" button: forces a rescan, at most every ConnectorManualScanIntervalSeconds;
    /// - "Add by name": one search within ConnectorNameSearchRadius, at most every ConnectorNameSearchIntervalSeconds.
    ///   Found connectors are pinned to the list until the session ends.
    /// Queries use MyGamePruningStructure.GetAllTopMostEntitiesInSphere with a reused list (no allocation).
    /// </summary>
    public class AnchorDirectory
    {
        private class ScanCache
        {
            public int LastScanFrame;
            public int LastCheckFrame;
            public int LastValidateFrame;
            public int LastManualScanFrame = int.MinValue / 2;
            public int LastNameSearchFrame = int.MinValue / 2;
            public Vector3D LastScanPosition;
            public bool HasScanned;
            public long DroneOwnerId;   // owner the list was built for
            public readonly List<AnchorEntry> Entries = new List<AnchorEntry>();
            public readonly List<long> Pinned = new List<long>();   // added by name
        }

        private const int VALIDATE_INTERVAL_TICKS = 60;

        private readonly Dictionary<long, ScanCache> caches = new Dictionary<long, ScanCache>();
        private readonly List<IMyCubeGrid> ownGroup = new List<IMyCubeGrid>();
        private readonly List<MyEntity> found = new List<MyEntity>();

        /// <summary>
        /// Cached anchors for this drone (see class notes for when it rescans). Entries whose block changed owner
        /// or left the faction are dropped (checked at most once a second).
        /// </summary>
        public List<AnchorEntry> Get(IMyCubeBlock drone)
        {
            var cfg = DroneConfig();
            ScanCache cache = GetCache(drone);
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (!cache.HasScanned || cache.DroneOwnerId != drone.OwnerId)
            {
                Scan(drone, cache, now);
                return cache.Entries;
            }
            if (now - cache.LastCheckFrame >= SecondsToFrames(cfg.ConnectorScanIntervalSeconds))
            {
                cache.LastCheckFrame = now;
                double move = cfg.ConnectorScanMinMoveMeters;
                bool moved = Vector3D.DistanceSquared(drone.WorldMatrix.Translation, cache.LastScanPosition) >= move * move;
                bool old = now - cache.LastScanFrame >= SecondsToFrames(cfg.ConnectorScanMaxAgeSeconds);
                if (moved || old)
                {
                    Scan(drone, cache, now);
                    return cache.Entries;
                }
            }
            if (now - cache.LastValidateFrame >= VALIDATE_INTERVAL_TICKS)
            {
                cache.LastValidateFrame = now;
                long owner = drone.OwnerId;
                for (int i = cache.Entries.Count - 1; i >= 0; i--)
                {
                    IMyEntity e;
                    var b = MyAPIGateway.Entities.TryGetEntityById(cache.Entries[i].EntityId, out e) ? e as IMyCubeBlock : null;
                    if (b == null || b.Closed || !IsAllowed(owner, b.OwnerId))
                        cache.Entries.RemoveAt(i);
                }
            }
            return cache.Entries;
        }

        /// <summary>
        /// Player-initiated scan. Returns false (and the seconds left) while throttled.
        /// </summary>
        public bool TryManualScan(IMyCubeBlock drone, out int secondsLeft)
        {
            ScanCache cache = GetCache(drone);
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (!Throttle(ref cache.LastManualScanFrame, now, DroneConfig().ConnectorManualScanIntervalSeconds, out secondsLeft))
                return false;
            Scan(drone, cache, now);
            return true;
        }

        /// <summary>
        /// Finds a connector by its exact name (case-insensitive) within ConnectorNameSearchRadius and pins it to
        /// the drone's list. Only the owner's / owner's faction's connectors match. There is no name index in the
        /// game, so this walks the connectors of every grid in range - hence the throttle.
        /// Returns: 1 added (or already listed), 0 not found, -1 throttled (secondsLeft set).
        /// </summary>
        public int TryAddByName(IMyCubeBlock drone, string name, out AnchorEntry entry, out int secondsLeft)
        {
            entry = default(AnchorEntry);
            secondsLeft = 0;
            if (drone == null || string.IsNullOrWhiteSpace(name) || drone.OwnerId == 0) return 0;
            ScanCache cache = GetCache(drone);
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (!Throttle(ref cache.LastNameSearchFrame, now, DroneConfig().ConnectorNameSearchIntervalSeconds, out secondsLeft))
                return -1;
            name = name.Trim();

            long owner = drone.OwnerId;
            IMyFaction ownerFaction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(owner);
            Vector3D center = drone.WorldMatrix.Translation;
            double radius = DroneConfig().ConnectorNameSearchRadius;
            QueryGrids(drone, center, radius);

            IMyShipConnector best = null;
            double bestD2 = double.MaxValue;
            for (int i = 0; i < found.Count; i++)
            {
                var grid = found[i] as IMyCubeGrid;
                if (grid == null) continue;
                foreach (var c in grid.GetFatBlocks<IMyShipConnector>())
                {
                    if (!c.IsFunctional || !string.Equals(c.CustomName, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!IsAllowed(owner, ownerFaction, c.OwnerId)) continue;
                    double d2 = Vector3D.DistanceSquared(center, c.WorldMatrix.Translation);
                    if (d2 <= radius * radius && d2 < bestD2) { best = c; bestD2 = d2; }   // same name twice: nearest
                }
            }
            found.Clear();
            if (best == null) return 0;

            entry = new AnchorEntry
            {
                EntityId = best.EntityId,
                Name = best.CustomName,
                Distance = Math.Sqrt(bestD2),
                Kind = AnchorKind.Connector,
                OwnerId = best.OwnerId,
            };
            if (!cache.Pinned.Contains(best.EntityId)) cache.Pinned.Add(best.EntityId);
            bool listed = false;
            for (int i = 0; i < cache.Entries.Count; i++)
                if (cache.Entries[i].EntityId == best.EntityId) { listed = true; break; }
            if (!listed) cache.Entries.Add(entry);
            return 1;
        }

        /// <summary>
        /// Resolves a stored id, re-applying the ownership rule and the range limit. Null when not allowed.
        /// </summary>
        public static IMyTerminalBlock Resolve(IMyCubeBlock drone, long entityId)
        {
            if (drone == null || entityId == 0) return null;
            IMyEntity e;
            if (!MyAPIGateway.Entities.TryGetEntityById(entityId, out e)) return null;
            var b = e as IMyTerminalBlock;
            if (b == null || b.Closed || b.CubeGrid?.Physics == null || !b.IsFunctional) return null;
            if (!(b is IMyShipConnector) && !(b is IMyBeacon)) return null;
            if (!IsAllowed(drone.OwnerId, b.OwnerId)) return null;
            double r = DroneConfig().ConnectorNameSearchRadius;
            if (Vector3D.DistanceSquared(drone.WorldMatrix.Translation, b.WorldMatrix.Translation) > r * r) return null;
            return b;
        }

        /// <summary>
        /// UI filter: the player looking at the terminal must also be the block's owner or in its faction
        /// (a drone shared with "All" must not reveal its owner's bases). True when there is no local player.
        /// Do not use in simulation code: every client would apply its own player.
        /// </summary>
        public static bool ViewerAllowed(long blockOwner)
        {
            var player = MyAPIGateway.Session?.Player;
            return player == null || IsAllowed(player.IdentityId, blockOwner);
        }

        public void Forget(long droneId)
        {
            caches.Remove(droneId);
        }

        public void Clear()
        {
            caches.Clear();
            found.Clear();
        }

        // Owner, or same faction as the owner. Unowned drones see nothing; unowned blocks are never listed.
        public static bool IsAllowed(long droneOwner, long blockOwner)
        {
            if (droneOwner == 0 || blockOwner == 0) return false;
            if (droneOwner == blockOwner) return true;
            var factions = MyAPIGateway.Session.Factions;
            IMyFaction mine = factions.TryGetPlayerFaction(droneOwner);
            return mine != null && factions.TryGetPlayerFaction(blockOwner) == mine;
        }

        private static bool IsAllowed(long droneOwner, IMyFaction ownerFaction, long blockOwner)
        {
            if (blockOwner == 0) return false;
            if (blockOwner == droneOwner) return true;
            return ownerFaction != null && MyAPIGateway.Session.Factions.TryGetPlayerFaction(blockOwner) == ownerFaction;
        }

        private ScanCache GetCache(IMyCubeBlock drone)
        {
            ScanCache cache;
            if (!caches.TryGetValue(drone.EntityId, out cache))
            {
                cache = new ScanCache();
                caches[drone.EntityId] = cache;
            }
            return cache;
        }

        // Top-most grids in the sphere into 'found', minus the drone's own (mechanically attached) grids and projections
        private void QueryGrids(IMyCubeBlock drone, Vector3D center, double radius)
        {
            found.Clear();
            var sphere = new BoundingSphereD(center, radius);
            MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref sphere, found, MyEntityQueryType.Both);
            ownGroup.Clear();
            if (drone.CubeGrid != null)
                drone.CubeGrid.GetGridGroup(GridLinkTypeEnum.Mechanical).GetGrids(ownGroup);
            for (int i = found.Count - 1; i >= 0; i--)
            {
                var grid = found[i] as IMyCubeGrid;
                if (grid == null || grid.Physics == null || grid.MarkedForClose || ownGroup.Contains(grid))
                    found.RemoveAtFast(i);
            }
            ownGroup.Clear();
        }

        private void Scan(IMyCubeBlock drone, ScanCache cache, int now)
        {
            cache.HasScanned = true;
            cache.LastScanFrame = now;
            cache.LastCheckFrame = now;
            cache.LastValidateFrame = now;
            cache.Entries.Clear();

            long owner = drone.OwnerId;
            cache.DroneOwnerId = owner;
            Vector3D center = drone.WorldMatrix.Translation;
            cache.LastScanPosition = center;
            if (owner == 0 || drone.CubeGrid == null) return;

            double radius = DroneConfig().ConnectorScanRadius;
            double r2 = radius * radius;
            IMyFaction ownerFaction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(owner);
            QueryGrids(drone, center, radius);

            for (int i = 0; i < found.Count; i++)
            {
                var grid = (IMyCubeGrid)found[i];
                foreach (var c in grid.GetFatBlocks<IMyShipConnector>())
                    TryAdd(cache, c, AnchorKind.Connector, owner, ownerFaction, ref center, r2);
                foreach (var b in grid.GetFatBlocks<IMyBeacon>())
                    TryAdd(cache, b, AnchorKind.Beacon, owner, ownerFaction, ref center, r2);
            }
            found.Clear();

            // Connectors added by name stay listed while they are still allowed and within the search radius
            for (int i = cache.Pinned.Count - 1; i >= 0; i--)
            {
                var b = Resolve(drone, cache.Pinned[i]) as IMyShipConnector;
                if (b == null) { cache.Pinned.RemoveAt(i); continue; }
                bool listed = false;
                for (int k = 0; k < cache.Entries.Count; k++)
                    if (cache.Entries[k].EntityId == b.EntityId) { listed = true; break; }
                if (!listed)
                    cache.Entries.Add(new AnchorEntry
                    {
                        EntityId = b.EntityId,
                        Name = b.CustomName,
                        Distance = Vector3D.Distance(center, b.WorldMatrix.Translation),
                        Kind = AnchorKind.Connector,
                        OwnerId = b.OwnerId,
                    });
            }
            SortByDistance(cache.Entries);
        }

        private static void TryAdd(ScanCache cache, IMyTerminalBlock b, AnchorKind kind, long owner, IMyFaction ownerFaction,
                                   ref Vector3D center, double r2)
        {
            if (b == null || !b.IsFunctional) return;
            if (!IsAllowed(owner, ownerFaction, b.OwnerId)) return;
            double d2 = Vector3D.DistanceSquared(center, b.WorldMatrix.Translation);
            if (d2 > r2) return;
            cache.Entries.Add(new AnchorEntry
            {
                EntityId = b.EntityId,
                Name = b.CustomName,
                Distance = Math.Sqrt(d2),
                Kind = kind,
                OwnerId = b.OwnerId,
            });
        }

        // Returns false (with the seconds left) if called again within 'intervalSeconds'
        private static bool Throttle(ref int lastFrame, int now, int intervalSeconds, out int secondsLeft)
        {
            int wait = SecondsToFrames(intervalSeconds);
            if (now - lastFrame < wait)
            {
                secondsLeft = (wait - (now - lastFrame)) / 60 + 1;
                return false;
            }
            lastFrame = now;
            secondsLeft = 0;
            return true;
        }

        private static Automata.Config.ServerConfig.DroneControllerBlockConfig DroneConfig()
        {
            return Automata.Config.ServerConfig.Instance.Drone;
        }

        // Insertion sort: System.Comparison<T> is not whitelisted, and the lists are short
        private static void SortByDistance(List<AnchorEntry> list)
        {
            for (int i = 1; i < list.Count; i++)
            {
                AnchorEntry e = list[i];
                int j = i - 1;
                while (j >= 0 && list[j].Distance > e.Distance)
                {
                    list[j + 1] = list[j];
                    j--;
                }
                list[j + 1] = e;
            }
        }

        private static int SecondsToFrames(int seconds)
        {
            return seconds * 60;
        }
    }
}
