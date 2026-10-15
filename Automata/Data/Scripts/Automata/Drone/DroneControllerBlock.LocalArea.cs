using System;

using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.VirtualNetwork;

namespace Automata.Drone
{
    /// <summary>
    /// Local jobs and coming back after them.
    ///  - Local-job radius (terminal, Jobs): the drone bids only on jobs whose site lies within it of its base: the home
    ///    connector, else where it last left from (dock / gears), else where it is. A relay or laser link joining two
    ///    bases far apart (orbit and ground) no longer sends drones of one to the other's jobs. A job ordered for this
    ///    drone by name (designator tag) is exempt.
    ///  - Coming back (no home connector): where it left from is remembered with the grid it was docked / parked on (or
    ///    the nearest friendly static grid when it left from voxel). After a job it rests there again: parks on that
    ///    grid first, or docks at a free connector of it (else the nearest free one near where it left from) - it used
    ///    to look for a spot / connector around wherever the job ended.
    /// </summary>
    public partial class DroneControllerBlock
    {
        /// <summary>Null: the auction's site is within the local-job radius (or has none / is ordered for this drone).</summary>
        private string OutsideLocalRadius(Auction auction)
        {
            if (settings == null || auction.OnlyDroneEntityId == _entityId) return null;
            Vector3D site = auction.PositionData.ToVector3D();
            if (site.LengthSquared() < 1) return null;   // no site given
            double r = MathHelper.Clamp(settings.LocalJobRadius, LOCAL_JOB_RADIUS_MIN, LOCAL_JOB_RADIUS_MAX);
            Vector3D basePoint = BasePoint();
            double d = Vector3D.Distance(site, basePoint);
            if (d <= r) return null;
            return string.Format("outside its local-job radius ({0:F0} m from its base, radius {1:F0} m)", d, r);
        }

        /// <summary>Where the drone belongs: its home connector, else where it last left from, else where it is.</summary>
        private Vector3D BasePoint()
        {
            var home = GetHomeConnector();
            if (home != null) return home.GetPosition();
            if (settings != null && settings.HasDeparture) return settings.DepartureSpot.ToVector3D();
            CaptureFlightState();
            return flightState.Position;
        }

        /// <summary>No home connector: the grid it left from (docked / parked on, or the nearest friendly static grid). Null: gone / none.</summary>
        private IMyCubeGrid DepartureGrid()
        {
            if (settings == null || HasHomeConnector || !settings.HasDeparture || settings.DepartureGridId == 0) return null;
            IMyEntity e;
            if (!MyAPIGateway.Entities.TryGetEntityById(settings.DepartureGridId, out e)) return null;
            var g = e as IMyCubeGrid;
            return g != null && !g.Closed && g.Physics != null && FriendlyGrid(g) ? g : null;
        }

        // Pre-flight, letting go: the grid it is held by now (connector, else a locked gear), else - on voxel or nothing -
        // the nearest friendly static grid around
        private long DepartureGridNow()
        {
            for (int i = 0; i < connectors.Count; i++)
            {
                var c = connectors[i];
                if (c != null && c.IsConnected && c.OtherConnector != null && !droneGrids.Contains(c.OtherConnector.CubeGrid))
                    return c.OtherConnector.CubeGrid.EntityId;
            }
            for (int i = 0; i < landingGears.Count; i++)
            {
                var gear = landingGears[i];
                if (gear == null || !gear.IsLocked) continue;
                var g = gear.GetAttachedEntity() as IMyCubeGrid;
                if (g != null && !droneGrids.Contains(g)) return g.EntityId;
            }
            Vector3D here = flightState.Position;
            var sphere = new BoundingSphereD(here, PARK_SEARCH_RADIUS);
            nearEntities.Clear();
            nearEntities.AddRange(MyAPIGateway.Entities.GetEntitiesInSphere(ref sphere));
            IMyCubeGrid best = null;
            double bestD = double.MaxValue;
            for (int i = 0; i < nearEntities.Count; i++)
            {
                var g = nearEntities[i] as IMyCubeGrid;
                if (g == null || !g.IsStatic || g.Physics == null || droneGrids.Contains(g) || !FriendlyGrid(g)) continue;
                double d = Vector3D.DistanceSquared(g.WorldVolume.Center, here) - g.WorldVolume.Radius * g.WorldVolume.Radius;
                if (d < bestD) { bestD = d; best = g; }
            }
            nearEntities.Clear();
            return best != null ? best.EntityId : 0;
        }
    }
}
