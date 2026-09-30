using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.Game;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

using Automata.Network;
using Automata.Util;
using BlendTypeEnum = VRageRender.MyBillboard.BlendTypeEnum;

namespace Automata.Drone
{
    /// <summary>
    /// Debug overlay "Draw navigation targets": red dots on the route's points (approach points, waypoints, final
    /// target), blue lines between them, starting at the point the active order steers. Local to the player who
    /// switched it on; off again after 2 minutes. The route lives on the server: single player / host draws it
    /// directly, multiplayer clients subscribe and get it (only when it changes) from the server.
    /// </summary>
    public partial class DroneControllerBlock
    {
        private const int NAV_DRAW_TICKS = 120 * 60;                  // switches itself off after 120 s
        private const int NAV_SUBSCRIPTION_TICKS = NAV_DRAW_TICKS + 10 * 60;   // server side, in case the "off" is lost
        private const double NAV_POINT_EPSILON = 0.1;                 // m: same point / not worth resending
        private static readonly MyStringId NavDotMaterial = MyStringId.GetOrCompute("WhiteDot");
        private static readonly Color NavDotColor = Color.Red;
        private static readonly Color NavLineColor = new Color(40, 110, 255);

        // This player's overlay
        private bool navDrawOn;
        private int navDrawStartFrame;
        private readonly List<Vector3D> navDrawPoints = new List<Vector3D>();
        private Vector3D navDrawOffset;                               // controller-local start of the route

        // Server: subscribed clients (steam id -> expiry frame) and what they were last sent
        private readonly Dictionary<ulong, int> navSubscribers = new Dictionary<ulong, int>();
        private readonly List<ulong> navSubscriberBuffer = new List<ulong>();
        private readonly List<Vector3D> navSendPoints = new List<Vector3D>();
        private readonly List<Vector3D> navSentPoints = new List<Vector3D>();
        private Vector3D navSentOffset;
        private bool navForceSend;
        private int navSendCounter;

        public bool Terminal_DrawNavigationTargets
        {
            get { return navDrawOn; }
            set
            {
                if (navDrawOn == value) return;
                navDrawOn = value;
                navDrawStartFrame = MyAPIGateway.Session.GameplayFrameCounter;
                if (!value) navDrawPoints.Clear();
                UpdateDrawRequest();
                if (!IsServer) RequestAction(new DroneActionPacket { Action = DroneAction.DrawNavigation, Code = value ? 1 : 0 });
            }
        }

        private void StopNavigationDraw()
        {
            navDrawOn = false;
            navDrawPoints.Clear();
        }

        // Session Draw(), clients / single player / host only. False when off.
        private bool DrawNavigationTargets()
        {
            if (!navDrawOn) return false;
            if (MyAPIGateway.Session.GameplayFrameCounter - navDrawStartFrame > NAV_DRAW_TICKS)
            {
                Terminal_DrawNavigationTargets = false;   // also tells the server to stop sending
                RefreshTerminal();
                return false;
            }
            if (shipController == null) return true;
            if (IsServer) BuildNavigationRoute(navDrawPoints, out navDrawOffset);
            if (navDrawPoints.Count == 0) return true;

            MatrixD wm = shipController.WorldMatrix;
            Vector3D camera = MyAPIGateway.Session.Camera != null ? MyAPIGateway.Session.Camera.Position : wm.Translation;
            Vector4 dot = NavDotColor.ToVector4();
            Vector4 line = NavLineColor.ToVector4();
            Vector3D prev = wm.Translation + Vector3D.TransformNormal(navDrawOffset, wm);
            for (int i = 0; i < navDrawPoints.Count; i++)
            {
                Vector3D p = navDrawPoints[i];
                // Sizes grow with distance so far routes stay visible
                float lineWidth = (float)MathHelper.Clamp(Vector3D.Distance(camera, (prev + p) * 0.5) * 0.002, 0.02, 0.5);
                MySimpleObjectDraw.DrawLine(prev, p, DrawMaterial, ref line, lineWidth, BlendTypeEnum.SDR);
                float radius = (float)MathHelper.Clamp(Vector3D.Distance(camera, p) * 0.006, 0.1, 3.0);
                MyTransparentGeometry.AddPointBillboard(NavDotMaterial, dot, p, radius, 0, -1, BlendTypeEnum.SDR);
                prev = p;
            }
            return true;
        }

        /// <summary>
        /// Server: the route the drone is flying, in order: the active order's approach point (while it is still
        /// heading there) and target, then the chained legs. World positions (anchored orders resolved now).
        /// 'referenceOffset': controller-local point the active order steers.
        /// </summary>
        private void BuildNavigationRoute(List<Vector3D> points, out Vector3D referenceOffset)
        {
            points.Clear();
            referenceOffset = Vector3D.Zero;
            var o = activeFlightOrder;
            if (o == null) return;
            referenceOffset = o.ReferenceOffset.ToVector3D();
            bool toApproach = o.Phase == FlightPhase.Transit || (o.Phase == FlightPhase.MatchSpeed && o.UseApproachLine);
            AddRoutePoints(o, toApproach, points);
            if (nextLeg != null) AddRoutePoints(nextLeg, nextLeg.UseApproachLine, points);
            foreach (var leg in queuedLegs) AddRoutePoints(leg, leg.UseApproachLine, points);
        }

        private void AddRoutePoints(FlightOrder o, bool withApproach, List<Vector3D> points)
        {
            Vector3D target = o.Target.ToVector3D(), approach = o.ApproachFrom.ToVector3D();
            if (o.AnchorEntityId != 0)
            {
                IMyTerminalBlock anchor = anchorBlock != null && anchorBlock.EntityId == o.AnchorEntityId
                    ? anchorBlock : AnchorDirectory.Resolve(block, o.AnchorEntityId);
                if (anchor != null)
                {
                    MatrixD m = anchor.WorldMatrix;
                    target = AnchorToWorldPoint(ref m, o.TargetLocal.ToVector3D());
                    approach = AnchorToWorldPoint(ref m, o.ApproachFromLocal.ToVector3D());
                }
            }
            if (withApproach) AddRoutePoint(approach, points);
            AddRoutePoint(target, points);
        }

        private static void AddRoutePoint(Vector3D p, List<Vector3D> points)
        {
            if (points.Count > 0 && Vector3D.DistanceSquared(points[points.Count - 1], p) < NAV_POINT_EPSILON * NAV_POINT_EPSILON) return;
            points.Add(p);
        }

        #region Multiplayer
        // Server (ExecuteAction): terminal access already checked
        private void SubscribeNavigationRoute(ulong steamId, bool on)
        {
            if (!IsMultiplayer || steamId == MyAPIGateway.Multiplayer.MyId) return;   // local player draws directly
            if (on)
            {
                navSubscribers[steamId] = MyAPIGateway.Session.GameplayFrameCounter + NAV_SUBSCRIPTION_TICKS;
                navForceSend = true;
            }
            else navSubscribers.Remove(steamId);
        }

        // Server, UpdateBeforeSimulation10: every 30 ticks, the route to subscribers when it changed
        private void SendNavigationRoutes()
        {
            if (navSubscribers.Count == 0) return;
            if (++navSendCounter < 3) return;
            navSendCounter = 0;
            var net = Net;
            if (net == null || !IsMultiplayer || Entity == null) { navSubscribers.Clear(); return; }

            int now = MyAPIGateway.Session.GameplayFrameCounter;
            navSubscriberBuffer.Clear();
            foreach (var kv in navSubscribers)
                if (now > kv.Value) navSubscriberBuffer.Add(kv.Key);
            for (int i = 0; i < navSubscriberBuffer.Count; i++) navSubscribers.Remove(navSubscriberBuffer[i]);
            navSubscriberBuffer.Clear();
            if (navSubscribers.Count == 0) return;

            Vector3D offset;
            BuildNavigationRoute(navSendPoints, out offset);
            if (!navForceSend && SameRoute(navSendPoints, navSentPoints) && Vector3D.DistanceSquared(offset, navSentOffset) < 1e-4) return;
            navForceSend = false;
            navSentPoints.Clear();
            navSentPoints.AddRange(navSendPoints);
            navSentOffset = offset;

            var packet = new DroneNavRoutePacket
            {
                EntityId = Entity.EntityId,
                Points = new List<Vector3DData>(navSendPoints.Count),
                ReferenceOffset = Vector3DData.FromVector3D(offset),
            };
            for (int i = 0; i < navSendPoints.Count; i++) packet.Points.Add(Vector3DData.FromVector3D(navSendPoints[i]));
            byte[] data = MyAPIGateway.Utilities.SerializeToBinary<Digi.NetworkLib.PacketBase>(packet);
            foreach (var kv in navSubscribers) net.SendToPlayer(null, kv.Key, data);
        }

        private static bool SameRoute(List<Vector3D> a, List<Vector3D> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (Vector3D.DistanceSquared(a[i], b[i]) > NAV_POINT_EPSILON * NAV_POINT_EPSILON) return false;
            return true;
        }

        // Client: route from the server (ignored once the overlay is off)
        public void ReceiveNavRoute(List<Vector3DData> points, Vector3DData referenceOffset)
        {
            if (IsServer || !navDrawOn) return;
            navDrawPoints.Clear();
            if (points != null)
                for (int i = 0; i < points.Count; i++)
                    if (IsFinite(points[i])) navDrawPoints.Add(points[i].ToVector3D());
            navDrawOffset = IsFinite(referenceOffset) ? referenceOffset.ToVector3D() : Vector3D.Zero;
        }
        #endregion
    }
}
