using System;
using System.Collections.Generic;

using ProtoBuf;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Orchestrator;
using Automata.Util;
using Automata.Util.Strings;

namespace Automata.Pathfinding
{
    /// <summary>A lane pair's lines for the drawing (flat x, y, z), server -> client.</summary>
    [ProtoContract]
    public class LaneShape
    {
        [ProtoMember(1)] public string NameAB;
        [ProtoMember(2)] public string NameBA;
        [ProtoMember(3)] public List<double> AB = new List<double>();
        [ProtoMember(4)] public List<double> BA = new List<double>();
        [ProtoMember(5)] public bool Pending;              // suspended: drawn faint
        [ProtoMember(6)] public List<double> UpAB = new List<double>();
        [ProtoMember(7)] public List<double> UpBA = new List<double>();
    }

    /// <summary>
    /// "/automata debugdraw lanes": the flight lanes of the orchestrators the player owns with a line within the range,
    /// drawn like the orchestrator's Visualize (dashes crawling toward each line's end, pending / suspended faint, names
    /// at the line starts as local GPS) until the server's DebugDrawTimeoutSeconds (real seconds: frames stretch with a low
    /// simulation speed; the server sends its value with the lanes) or "debugdraw off". The lanes live on the server:
    /// Collect there (multiplayer: LaneDrawRequestPacket), Show on the player's client.
    /// </summary>
    public sealed class LaneOverlay : IDebugDrawable
    {
        private static LaneOverlay instance;
        private readonly List<LaneShape> shapes = new List<LaneShape>();
        private readonly List<IMyGps> gps = new List<IMyGps>();
        private DateTime until;

        /// <summary>Server: the lanes owned by 'identity's orchestrators with a line within 'range' of 'centre'.</summary>
        public static List<LaneShape> Collect(long identity, Vector3D centre, double range)
        {
            var list = new List<LaneShape>();
            var pm = PathfindingManager.Instance;
            if (pm == null || pm.Lanes == null || identity == 0) return list;
            var near = new List<FlightLanes.Lane>();
            pm.Lanes.Near(centre, range, near);
            for (int i = 0; i < near.Count; i++)
            {
                var l = near[i];
                IMyEntity e;
                var owner = MyAPIGateway.Entities.TryGetEntityById(l.Owner, out e) ? e as IMyTerminalBlock : null;
                if (owner == null || owner.OwnerId != identity) continue;
                var s = new LaneShape { NameAB = l.Name(true), NameBA = l.Name(false), Pending = l.State != FlightLanes.LaneState.Active };
                OrchestratorBlock.Flatten(l.AB, s.AB);
                OrchestratorBlock.Flatten(l.BA, s.BA);
                OrchestratorBlock.Flatten(l.UpAB, s.UpAB);
                OrchestratorBlock.Flatten(l.UpBA, s.UpBA);
                list.Add(s);
            }
            return list;
        }

        /// <summary>Client: draws these, in place of the last ones.</summary>
        public static void Show(List<LaneShape> lanes, double range, int timeoutSeconds)
        {
            if (instance == null) instance = new LaneOverlay();
            instance.StopDebugDraw();
            int n = lanes != null ? lanes.Count : 0;
            MyAPIGateway.Utilities.ShowMessage(AutomataSession.MOD_NAME, Loc.F(MyStrings.Common_DebugDrawLanesWithinM, n, (int)range));
            if (n == 0 || AutomataSession.Instance == null) return;
            instance.shapes.AddRange(lanes);
            instance.until = DateTime.UtcNow.AddSeconds(MathHelper.Clamp(timeoutSeconds, 10, 3600));
            instance.AddGps();
            AutomataSession.Instance.RequestDraw(instance, true);
        }

        public bool DrawDebug()
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (shapes.Count == 0 || DateTime.UtcNow > until)
            {
                StopDebugDraw();
                return false;
            }
            float phase = (now / 60f * OrchestratorBlock.LANE_CRAWL) % (OrchestratorBlock.LANE_DASH + OrchestratorBlock.LANE_GAP);
            for (int i = 0; i < shapes.Count; i++)
            {
                float alpha = shapes[i].Pending ? 0.35f : 0.9f;
                OrchestratorBlock.DrawBox(shapes[i].AB, shapes[i].UpAB, Color.DeepSkyBlue * alpha, phase);
                OrchestratorBlock.DrawBox(shapes[i].BA, shapes[i].UpBA, Color.Orange * alpha, phase);
            }
            return true;
        }

        public void StopDebugDraw()
        {
            shapes.Clear();
            if (MyAPIGateway.Session != null && MyAPIGateway.Session.GPS != null)
                for (int i = 0; i < gps.Count; i++) MyAPIGateway.Session.GPS.RemoveLocalGps(gps[i]);
            gps.Clear();
        }

        // The lines' names at their starts, as local GPS markers while drawn
        private void AddGps()
        {
            if (MyAPIGateway.Session == null || MyAPIGateway.Session.GPS == null) return;
            for (int i = 0; i < shapes.Count; i++)
            {
                AddEndGps(shapes[i].AB, shapes[i].NameAB, Color.DeepSkyBlue);
                AddEndGps(shapes[i].BA, shapes[i].NameBA, Color.Orange);
            }
        }

        private void AddEndGps(List<double> flat, string name, Color color)
        {
            if (flat == null || flat.Count < 3) return;
            var g = MyAPIGateway.Session.GPS.Create(name ?? "lane", "", new Vector3D(flat[0], flat[1], flat[2]), true, true);
            g.GPSColor = color;
            MyAPIGateway.Session.GPS.AddLocalGps(g);
            gps.Add(g);
        }

    }
}
