using System;
using System.Collections.Generic;

using ProtoBuf;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;
using BlendTypeEnum = VRageRender.MyBillboard.BlendTypeEnum;

using Automata.Util;
using Automata.Util.Strings;

namespace Automata.Pathfinding
{
    /// <summary>A custom route's points for the drawing (flat x, y, z), server -> client.</summary>
    [ProtoContract]
    public class RouteShape
    {
        [ProtoMember(1)] public int Id;
        [ProtoMember(2)] public string Name;
        [ProtoMember(3)] public List<double> Points = new List<double>();
        [ProtoMember(4)] public List<double> Fwds = new List<double>();   // each point's recorded attitude (zero: none)
        [ProtoMember(5)] public List<double> Ups = new List<double>();
    }

    /// <summary>
    /// "/automata debugdraw customroute": the players' recorded custom routes (RouteMemory) the player's drones may use with
    /// a stretch within the range, drawn like a drone's navigation points (orange lines) with their names at their starts
    /// (local GPS), until the server's DebugDrawTimeoutSeconds or "debugdraw off". The routes live on the server: Collect
    /// there (multiplayer: CustomRouteDrawRequestPacket), Show on the player's client. DrawRoute is also the orchestrator's.
    /// </summary>
    public sealed class CustomRouteOverlay : IDebugDrawable
    {
        private static readonly MyStringId LineMaterial = MyStringId.GetOrCompute("Square");
        private static readonly MyStringId DotMaterial = MyStringId.GetOrCompute("WhiteDot");
        internal static readonly Color LineColor = Color.Orange;
        private static readonly Color DotColor = Color.Red;

        private static CustomRouteOverlay instance;
        private readonly List<RouteShape> shapes = new List<RouteShape>();
        private readonly List<IMyGps> gps = new List<IMyGps>();
        private DateTime until;

        /// <summary>Server: the custom routes 'identity's drones may use with a stretch within 'range' of 'centre'.</summary>
        public static List<RouteShape> Collect(long identity, Vector3D centre, double range)
        {
            var list = new List<RouteShape>();
            var pm = PathfindingManager.Instance;
            if (pm == null || pm.Memory == null || identity == 0) return list;
            var f = MyAPIGateway.Session != null && MyAPIGateway.Session.Factions != null ? MyAPIGateway.Session.Factions.TryGetPlayerFaction(identity) : null;
            var routes = new List<RouteMemory.CustomRoute>();
            pm.Memory.RoutesOf(identity, f != null ? f.FactionId : 0, routes);
            var pts = new List<Vector3D>();
            var fwds = new List<Vector3D>();
            var ups = new List<Vector3D>();
            double r2 = range * range;
            for (int i = 0; i < routes.Count; i++)
            {
                double length;
                if (!pm.Memory.RoutePoints(routes[i].Id, pts, fwds, ups, out length)) continue;
                bool near = pts.Count == 1 && Vector3D.DistanceSquared(pts[0], centre) <= r2;
                for (int k = 0; k + 1 < pts.Count && !near; k++) near = SegmentDistanceSq(pts[k], pts[k + 1], centre) <= r2;
                if (!near) continue;
                var s = new RouteShape { Id = routes[i].Id, Name = Label(routes[i]) };
                Orchestrator.OrchestratorBlock.Flatten(pts, s.Points);
                Orchestrator.OrchestratorBlock.Flatten(fwds, s.Fwds);
                Orchestrator.OrchestratorBlock.Flatten(ups, s.Ups);
                list.Add(s);
            }
            return list;
        }

        internal static string Label(RouteMemory.CustomRoute r)
        {
            return string.IsNullOrEmpty(r.Name) ? "R" + r.Id : "R" + r.Id + " " + r.Name;
        }

        private static double SegmentDistanceSq(Vector3D a, Vector3D b, Vector3D p)
        {
            Vector3D ab = b - a;
            double len2 = ab.LengthSquared();
            double t = len2 > 1e-9 ? MathHelperD.Clamp(Vector3D.Dot(p - a, ab) / len2, 0, 1) : 0;
            return Vector3D.DistanceSquared(p, a + ab * t);
        }

        /// <summary>Client: draws these, in place of the last ones.</summary>
        public static void Show(List<RouteShape> routes, double range, int timeoutSeconds)
        {
            if (instance == null) instance = new CustomRouteOverlay();
            instance.StopDebugDraw();
            int n = routes != null ? routes.Count : 0;
            MyAPIGateway.Utilities.ShowMessage(AutomataSession.MOD_NAME, Loc.F(MyStrings.Common_DebugDrawCustomRoutesWithinM, n, (int)range));
            if (n == 0 || AutomataSession.Instance == null) return;
            instance.shapes.AddRange(routes);
            instance.until = DateTime.UtcNow.AddSeconds(MathHelper.Clamp(timeoutSeconds, 10, 3600));
            if (MyAPIGateway.Session != null && MyAPIGateway.Session.GPS != null)
                for (int i = 0; i < instance.shapes.Count; i++)
                {
                    var g = StartGps(instance.shapes[i].Points, instance.shapes[i].Name);
                    if (g != null) instance.gps.Add(g);
                }
            AutomataSession.Instance.RequestDraw(instance, true);
        }

        public bool DrawDebug()
        {
            if (shapes.Count == 0 || DateTime.UtcNow > until)
            {
                StopDebugDraw();
                return false;
            }
            for (int i = 0; i < shapes.Count; i++) DrawRoute(shapes[i].Points, shapes[i].Fwds, shapes[i].Ups);
            return true;
        }

        public void StopDebugDraw()
        {
            shapes.Clear();
            if (MyAPIGateway.Session != null && MyAPIGateway.Session.GPS != null)
                for (int i = 0; i < gps.Count; i++) MyAPIGateway.Session.GPS.RemoveLocalGps(gps[i]);
            gps.Clear();
        }

        /// <summary>A route's name at its start, as a local GPS marker (null: none).</summary>
        internal static IMyGps StartGps(List<double> flat, string name)
        {
            if (flat == null || flat.Count < 3 || MyAPIGateway.Session == null || MyAPIGateway.Session.GPS == null) return null;
            var g = MyAPIGateway.Session.GPS.Create(name ?? "route", "", new Vector3D(flat[0], flat[1], flat[2]), true, true);
            g.GPSColor = LineColor;
            MyAPIGateway.Session.GPS.AddLocalGps(g);
            return g;
        }

        private static readonly Color ForwardColor = Color.Cyan, UpColor = Color.Lime;
        private const double ARROW_FORWARD = 1.5, ARROW_UP = 1.0;   // m

        /// <summary>A route like a drone's navigation points: its line (orange), a dot at each point; where a point has a
        /// recorded attitude ('fwds' / 'ups', flat, zero: none), its forward (cyan) and up (green) as small arrows. Sizes
        /// grow with the distance so far routes stay visible.</summary>
        internal static void DrawRoute(List<double> flat, List<double> fwds = null, List<double> ups = null)
        {
            int n = flat != null ? flat.Count / 3 : 0;
            if (n == 0) return;
            Vector3D camera = MyAPIGateway.Session.Camera != null ? MyAPIGateway.Session.Camera.Position : new Vector3D(flat[0], flat[1], flat[2]);
            Vector4 line = LineColor.ToVector4(), dot = DotColor.ToVector4();
            Vector3D prev = new Vector3D(flat[0], flat[1], flat[2]);
            for (int i = 0; i < n; i++)
            {
                var p = new Vector3D(flat[3 * i], flat[3 * i + 1], flat[3 * i + 2]);
                if (i > 0)
                {
                    float width = (float)MathHelper.Clamp(Vector3D.Distance(camera, (prev + p) * 0.5) * 0.002, 0.02, 0.5);
                    MySimpleObjectDraw.DrawLine(prev, p, LineMaterial, ref line, width, BlendTypeEnum.SDR);
                }
                float radius = (float)MathHelper.Clamp(Vector3D.Distance(camera, p) * 0.006, 0.1, 3.0);
                MyTransparentGeometry.AddPointBillboard(DotMaterial, dot, p, radius, 0, -1, BlendTypeEnum.SDR);
                if (fwds != null && ups != null && fwds.Count >= 3 * n && ups.Count >= 3 * n)
                {
                    var f = new Vector3D(fwds[3 * i], fwds[3 * i + 1], fwds[3 * i + 2]);
                    var u = new Vector3D(ups[3 * i], ups[3 * i + 1], ups[3 * i + 2]);
                    float thin = (float)MathHelper.Clamp(Vector3D.Distance(camera, p) * 0.0012, 0.02, 0.2);
                    if (f.LengthSquared() > 0.5) Arrow(p, Vector3D.Normalize(f), ARROW_FORWARD, u, ForwardColor, thin);
                    if (u.LengthSquared() > 0.5) Arrow(p, Vector3D.Normalize(u), ARROW_UP, f, UpColor, thin);
                }
                prev = p;
            }
        }

        // A line from p along 'dir' with a small head (two strokes back from its tip, in the plane of 'side')
        private static void Arrow(Vector3D p, Vector3D dir, double length, Vector3D side, Color color, float thickness)
        {
            Vector4 c = color.ToVector4();
            Vector3D tip = p + dir * length;
            MySimpleObjectDraw.DrawLine(p, tip, LineMaterial, ref c, thickness, BlendTypeEnum.SDR);
            Vector3D s = side - dir * Vector3D.Dot(side, dir);
            if (s.LengthSquared() < 1e-4) s = Vector3D.CalculatePerpendicularVector(dir);
            s.Normalize();
            double head = length * 0.25;
            MySimpleObjectDraw.DrawLine(tip, tip - dir * head + s * head * 0.6, LineMaterial, ref c, thickness, BlendTypeEnum.SDR);
            MySimpleObjectDraw.DrawLine(tip, tip - dir * head - s * head * 0.6, LineMaterial, ref c, thickness, BlendTypeEnum.SDR);
        }
    }
}
