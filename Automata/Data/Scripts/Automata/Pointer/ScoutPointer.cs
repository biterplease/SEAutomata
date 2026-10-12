using System;
using System.Collections.Generic;

using ProtoBuf;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Weapons;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

using Automata.Orchestrator;
using Automata.Util;
using Automata.Util.Logging;
using Digi.NetworkLib;

namespace Automata.Pointer
{
    public enum PointerKind : byte { None, Combat, Industry }

    /// <summary>Designator modes. Values are on the wire (ScoutPointerPacket): Tag was added last, the cycle order is
    /// ScoutPointer.MODE_CYCLE (tag first).</summary>
    public enum PointerMode : byte { Red, Blue, Green, Tag }

    /// <summary>
    /// What the server tells the firing player (the held designator's 5 status lights blink, a sound may play):
    /// Error: red once (error sound). TagOk: green x3 (ok sound). Requested (an order sent to the orchestrator): yellow x3,
    /// the request sound (the ok sound's first chirp). ConfirmedGreen / ConfirmedBlue (all its jobs have a drone: spot check / engage green, area sweep / escort
    /// blue): x3 (ok sound). Recall: darker green x3 (ok sound). Values are sent.
    /// </summary>
    public enum DesignatorSignal : byte { Error = 0, TagOk = 1, Requested = 2, ConfirmedGreen = 3, ConfirmedBlue = 4, Recall = 5 }

    /// <summary>
    /// Scout laser pointers (TODO/11): the hand tools, their mode cycles with the player's Reload control, and a
    /// trigger pull raycasts from the camera and sends the hit (position, entity) to the server, which orders the
    /// job at the player's orchestrator. Orders show the player a GPS marker while they are out (no beam):
    /// OrchestratorBlock.PlayerJobs.
    ///
    /// How the Reload key is ours while a pointer is held (no "ammo types" needed):
    ///  - Mods can't rebind controls (IMyControl has no binding setter), but can read any control, whatever key the
    ///    player bound to it.
    ///  - Vanilla (MyCharacter) enables RELOAD only while the held weapon's CanReload() is true, which needs the
    ///    magazine not full and a spare magazine in the inventory. The pointer's magazine has capacity 1, is kept full
    ///    here every tick, and is not craftable: CanReload() stays false, vanilla never reloads or plays the animation,
    ///    and disables RELOAD every frame.
    ///  - So the control is read with its enabled flag forced on for the read (as PaintGun does for its controls).
    ///  - With no magazine in the inventory, vanilla can't shoot either (out of ammo; creative: harmless 0-damage,
    ///    0-range shots). The trigger is read the same way and handled here.
    /// </summary>
    public static class ScoutPointer
    {
        #region UI strings
        private static readonly MyStringId StrEngageTarget = MyStringId.GetOrCompute("engage {0}");
        private static readonly MyStringId StrEscortTarget = MyStringId.GetOrCompute("escort {0}");
        private static readonly MyStringId StrSpotCheck = MyStringId.GetOrCompute("spot check");
        private static readonly MyStringId StrHostile = MyStringId.GetOrCompute("hostile");
        private static readonly MyStringId StrIndustrial = MyStringId.GetOrCompute("industrial");
        private static readonly MyStringId StrSpotCheckN = MyStringId.GetOrCompute("spot check {0}/{1}");
        private static readonly MyStringId StrSpotCheckMarker = MyStringId.GetOrCompute("Spot check");
        private static readonly MyStringId StrAreaSweepSector = MyStringId.GetOrCompute("area sweep sector {0}/{1}");
        private static readonly MyStringId StrAreaSweep = MyStringId.GetOrCompute("Area sweep");
        private static readonly MyStringId StrEngageAtAPoint = MyStringId.GetOrCompute("engage at a point");
        private static readonly MyStringId StrADesignatedPoint = MyStringId.GetOrCompute("a designated point");
        private static readonly MyStringId StrScoutLaser = MyStringId.GetOrCompute("scout laser");
        #endregion

        // Hand item subtypes (MyObjectBuilder_AutomaticRifle): the held entity's DefinitionId
        public const string COMBAT_HAND_ITEM = "AutomataHostileTargetDesignator";
        public const string INDUSTRY_HAND_ITEM = "AutomataIndustrialTargetDesignator";
        public const double RAY_LENGTH = 15000;
        /// <summary>Animation event of the Reload (mode cycle) animation: TUNE_EVENT in tools/gen_pointer_animations.py.</summary>
        public const string TUNE_ANIMATION_EVENT = "reload_weapon";

        /// <summary>Both designators charge this long (their sound, Data/Audio.sbc: 1.72 s) while the trigger is held: the
        /// raycast and the order happen when the charge ends, where the player aims then. Released before: cancelled.
        /// After a shot the trigger must be released before the next charge.</summary>
        public const float CHARGE_SECONDS = 1.72f;
        /// <summary>Tag mode charges in this share of CHARGE_SECONDS, silently (only the ok / error sound answers).</summary>
        public const float TAG_CHARGE_SHARE = 0.25f;

        /// <summary>The Reload control's cycle, both designators: tag, then (hostile) engage, escort, recall / (industrial)
        /// spot check, area sweep, recall.</summary>
        public static readonly PointerMode[] MODE_CYCLE = { PointerMode.Tag, PointerMode.Red, PointerMode.Blue, PointerMode.Green };

        public static int ModeIndex(PointerMode mode)
        {
            for (int i = 0; i < MODE_CYCLE.Length; i++) if (MODE_CYCLE[i] == mode) return i;
            return 0;
        }

        public static float ChargeSeconds(PointerMode mode)
        {
            return mode == PointerMode.Tag ? CHARGE_SECONDS * TAG_CHARGE_SHARE : CHARGE_SECONDS;
        }

        private static readonly List<IMyAutomaticRifleGun> rifles = new List<IMyAutomaticRifleGun>();
        private static readonly PointerMode[] modes = { PointerMode.Tag, PointerMode.Tag, PointerMode.Tag };   // per kind, this player's (tag first)
        private static bool loaded;

        public static PointerKind KindOf(IMyEntity e)
        {
            var rifle = e as IMyAutomaticRifleGun;
            if (rifle == null) return PointerKind.None;
            string s = rifle.DefinitionId.SubtypeName;
            return s == COMBAT_HAND_ITEM ? PointerKind.Combat : s == INDUSTRY_HAND_ITEM ? PointerKind.Industry : PointerKind.None;
        }

        public static void Load(string modPath)
        {
            if (loaded) return;
            loaded = true;
            DesignatorVisuals.Load(modPath);
            MyAPIGateway.Entities.OnEntityAdd += EntityAdded;
            // Pointers already held when the world loaded (spawned before this ran)
            MyAPIGateway.Entities.GetEntities(null, e =>
            {
                var ch = e as IMyCharacter;
                if (ch != null && ch.EquippedTool != null) EntityAdded(ch.EquippedTool);
                return false;
            });
        }

        public static void Unload()
        {
            if (!loaded) return;
            loaded = false;
            tags.Clear();
            localTagCount = 0;
            localTagKind = PointerKind.None;
            MyAPIGateway.Entities.OnEntityAdd -= EntityAdded;
            charging = false;
            RestoreHandIk();
            DesignatorVisuals.Unload();
            rifles.Clear();
        }

        private static void EntityAdded(IMyEntity e)
        {
            if (KindOf(e) == PointerKind.None) return;
            var rifle = (IMyAutomaticRifleGun)e;
            if (!rifles.Contains(rifle)) rifles.Add(rifle);
            Log.Debug("Pointer: {0} spawned (entity {1})", KindOf(e), e.EntityId);
        }

        // The rifle pose comes from Data/AnimationControllers/Automata_AC_Astronaut*.sbc (generated by
        // tools/gen_pointer_animations.py): own layers keyed on the hand item variables, as PaintGun does.

        /// <summary>Every tick (server and clients): pointers' magazines kept full, so vanilla never reloads them.</summary>
        public static void Update()
        {
            for (int i = rifles.Count - 1; i >= 0; i--)
            {
                var r = rifles[i];
                if (r == null || r.MarkedForClose || r.Closed) { rifles.RemoveAt(i); continue; }
                if (r.GunBase != null && r.GunBase.CurrentAmmo < 1) r.GunBase.CurrentAmmo = 1;
            }
            if (charging) UpdateCharge();
            if (ikOffDefinition != null && MyAPIGateway.Session != null && MyAPIGateway.Session.ElapsedPlayTime.TotalSeconds >= ikOffUntil)
                RestoreHandIk();
        }

        #region Left-hand IK off during the Reload (mode cycle) animation (client)
        /// <summary>Length of the Reload animation (Automata_designator_*_adjust clips: 3.33 s at speed 1). The left-hand
        /// IK comes back when it ends.</summary>
        public const double TUNE_ANIMATION_SECONDS = 2.6;

        // Why: after the animation controller, MyCharacter.UpdateHandBones pins the left arm (SE_RigLUpperarm ..
        // SE_RigLPalm, IK) to the hand item's LeftHandPosition / Orientation every frame while the hand item's
        // SimulateLeftHand (third person) / SimulateLeftHandFps (first person) is true (default) and the gun is not
        // reloading: vanilla turns it off when the gun's IsReloading turns true and back on at the track's
        // MagazinePlug event. The pointer never really reloads, so the hand would stay glued to the handguard while
        // the rest of the arm animates. The game reads the flags from the definition every frame, so they are turned
        // off on it for the animation's length. Local: the definition is shared by every character holding that
        // pointer on this client (others holding one lose the IK for that time too); other clients keep theirs.
        private static MyHandItemDefinition ikOffDefinition;
        private static bool ikOffLeft, ikOffLeftFps;   // the definition's own values, put back afterwards
        private static double ikOffUntil;

        private static void HandIkOff(IMyAutomaticRifleGun tool)
        {
            if (tool == null || MyDefinitionManager.Static == null) return;
            var id = tool.DefinitionId;
            var def = MyDefinitionManager.Static.TryGetHandItemDefinition(ref id);
            if (def == null) return;
            if (ikOffDefinition != def)
            {
                RestoreHandIk();   // the other pointer's, if it was still off
                ikOffDefinition = def;
                ikOffLeft = def.SimulateLeftHand;
                ikOffLeftFps = def.SimulateLeftHandFps;
            }
            def.SimulateLeftHand = false;
            def.SimulateLeftHandFps = false;
            ikOffUntil = MyAPIGateway.Session.ElapsedPlayTime.TotalSeconds + TUNE_ANIMATION_SECONDS;   // pressed again: from now
        }

        private static void RestoreHandIk()
        {
            if (ikOffDefinition == null) return;
            ikOffDefinition.SimulateLeftHand = ikOffLeft;
            ikOffDefinition.SimulateLeftHandFps = ikOffLeftFps;
            ikOffDefinition = null;
        }
        #endregion

        #region Charge (client: sound and lights, then the shot)
        private static bool charging;
        private static PointerKind chargeKind;
        private static PointerMode chargeMode;
        private static IMyAutomaticRifleGun chargeTool;
        private static double chargeStart;
        private static bool needsRelease;   // fired: the trigger must be let go before the next charge

        private static void StartCharge(PointerKind kind, PointerMode mode, IMyAutomaticRifleGun tool)
        {
            charging = true;
            chargeKind = kind;
            chargeMode = mode;
            chargeTool = tool;
            chargeStart = MyAPIGateway.Session.ElapsedPlayTime.TotalSeconds;
            DesignatorVisuals.StopBlink(tool);   // a tag's result blink gives way to the next charge
            if (mode != PointerMode.Tag) DesignatorVisuals.PlayChargeSound(tool);   // tag mode: only the ok / error sound
            DesignatorVisuals.SetCharge(tool, 0f, mode);
            Log.Debug("Pointer: {0} {1} ({2}) charging", kind, mode, ModeName(kind, mode));
        }

        // Every tick while charging: lights follow the charge; at its end the shot, where the player aims then.
        // Put away / died / no longer in control: cancelled (trigger released: HandleInput).
        private static void UpdateCharge()
        {
            var session = MyAPIGateway.Session;
            var character = session != null && session.Player != null ? session.Player.Character : null;
            if (character == null || character.IsDead || character.EquippedTool != chargeTool || session.ControlledObject != character)
            {
                CancelCharge("put away");
                return;
            }
            float progress = (float)((session.ElapsedPlayTime.TotalSeconds - chargeStart) / ChargeSeconds(chargeMode));
            DesignatorVisuals.SetCharge(chargeTool, progress, chargeMode);
            if (progress < 1f) return;
            charging = false;
            needsRelease = true;
            DesignatorVisuals.SetCharge(chargeTool, -1f, chargeMode);
            Fire(chargeKind, chargeMode, character);
        }

        private static void CancelCharge(string why)
        {
            if (!charging) return;
            charging = false;
            DesignatorVisuals.StopChargeSound(chargeTool);
            if (chargeTool != null && !chargeTool.Closed) DesignatorVisuals.SetCharge(chargeTool, -1f, chargeMode);
            Log.Debug("Pointer: charge cancelled ({0})", why);
        }
        #endregion

        /// <summary>Clients, every frame (session Draw): the designators' knobs and lights; the held one shows the local
        /// player's mode and tagged drones (tag light, counter wheels).</summary>
        public static void Draw()
        {
            var held = HeldDesignator();
            var kind = KindOf(held);
            DesignatorVisuals.Draw(rifles, held, kind != PointerKind.None ? modes[(int)kind] : MODE_CYCLE[0],
                                   kind != PointerKind.None && kind == localTagKind ? localTagCount : 0);
        }

        // The local player's equipped designator, or null
        private static IMyAutomaticRifleGun HeldDesignator()
        {
            var player = MyAPIGateway.Session != null ? MyAPIGateway.Session.Player : null;
            var held = player != null && player.Character != null ? player.Character.EquippedTool as IMyAutomaticRifleGun : null;
            return KindOf(held) != PointerKind.None ? held : null;
        }

        /// <summary>Clients, every frame (session HandleInput): the local player's pointer.</summary>
        public static void HandleInput()
        {
            var session = MyAPIGateway.Session;
            if (session == null || session.Player == null) return;
            var character = session.Player.Character;
            var kind = character != null ? KindOf(character.EquippedTool) : PointerKind.None;
            // Holstered / swapped (another tool, the other designator, none): the tag memory is forgotten
            if (localTagCount > 0 && kind != localTagKind) ClearTags("put away");
            if (character == null || session.ControlledObject != character || session.IsCameraUserControlledSpectator || kind == PointerKind.None
                || MyAPIGateway.Gui.ChatEntryVisible || MyAPIGateway.Gui.IsCursorVisible)
            {
                CancelCharge("no longer aiming");
                needsRelease = false;
                return;
            }

            var tool = character.EquippedTool as IMyAutomaticRifleGun;
            // While the Reload animation plays: no new Reload (it would restart the knob turn) and no shot
            bool tuning = DesignatorVisuals.IsTuning(tool);
            if (!charging && !tuning && ReadControl(MyControlsSpace.RELOAD, true))
            {
                int k = (int)kind;
                modes[k] = MODE_CYCLE[(ModeIndex(modes[k]) + 1) % MODE_CYCLE.Length];   // the tag memory stays
                // The Reload animation (animation controller, event TUNE_ANIMATION_EVENT): locally first (the synced
                // call may only reach the others), then synced for the other players. The knob follows it
                // (DesignatorVisuals: TUNER_GRAB_FRAME .. TUNER_RELEASE_FRAME).
                ExitIronsight(character, tool);   // the Reload animation is played from the hip
                character.TriggerCharacterAnimationEvent(TUNE_ANIMATION_EVENT, false);
                character.TriggerCharacterAnimationEvent(TUNE_ANIMATION_EVENT, true);
                HandIkOff(tool);   // or the left hand stays on the handguard (see HandIkOff)
                DesignatorVisuals.StartTune(tool, modes[k]);
                Log.Debug("Pointer: {0} mode -> {1} ({2}) [Reload control]", kind, modes[k], ModeName(kind, modes[k]));
            }
            if (tool != null)
            {
                // Both designators: held trigger charges (sound, lights) and acts when the charge ends (UpdateCharge);
                // let go before: cancelled; after a shot it must be let go before the next charge
                // (pulled during the Reload animation: ignored, and must be let go and pulled again afterwards)
                bool held = ReadControl(MyControlsSpace.PRIMARY_TOOL_ACTION, false);
                if (!held) { needsRelease = false; CancelCharge("trigger released"); }
                else if (tuning) needsRelease = true;
                else if (!charging && !needsRelease) StartCharge(kind, modes[(int)kind], tool);
            }
        }

        /// <summary>
        /// Aiming down the sights: leave it. The rifle's secondary action toggles the ironsight (as PaintGun reverts an
        /// accidental one), so it is triggered once when the character is in it.
        /// </summary>
        private static void ExitIronsight(IMyCharacter character, IMyAutomaticRifleGun tool)
        {
            if (character == null || tool == null) return;
            var pos = character.Components.Get<Sandbox.Game.Entities.Character.Components.MyCharacterWeaponPositionComponent>();
            if (pos == null || !pos.IsInIronSight) return;
            tool.Shoot(MyShootActionEnum.SecondaryAction, Vector3.Forward, null, null);
            tool.EndShoot(MyShootActionEnum.SecondaryAction);
        }

        // A control held ('justPressed': pressed this frame), whatever vanilla did with its enabled flag this frame
        private static bool ReadControl(MyStringId controlId, bool justPressed)
        {
            var control = MyAPIGateway.Input.GetGameControl(controlId);
            if (control == null) return false;
            bool was = control.IsEnabled;
            try
            {
                control.IsEnabled = true;
                return justPressed ? control.IsNewPressed() : control.IsPressed();
            }
            finally
            {
                control.IsEnabled = was;
            }
        }

        #region Tag memory, client side (the server keeps the list: TagMemory)
        private static int localTagCount;            // drones the server holds for this player (designator light)
        private static PointerKind localTagKind;

        /// <summary>The server's count of this player's tagged drones (0: memory empty / cleared).</summary>
        public static void TagCountReceived(PointerKind kind, int count)
        {
            localTagKind = count > 0 ? kind : PointerKind.None;
            localTagCount = count;
        }

        // Mode change, holstered / swapped: forgotten here and on the server (it also clears when the mode / designator
        // of a pull differs from the memory's, so a lost packet changes nothing)
        private static void ClearTags(string why)
        {
            if (localTagCount == 0) return;
            localTagCount = 0;
            localTagKind = PointerKind.None;
            Log.Debug("Pointer: tagged drones forgotten ({0})", why);
            var net = AutomataSession.Instance != null ? AutomataSession.Instance.Net : null;
            if (net != null) net.SendToServer(new ScoutPointerClearTagsPacket());
        }
        #endregion

        private static void Fire(PointerKind kind, PointerMode mode, IMyCharacter character)
        {
            MatrixD cam = MyAPIGateway.Session.Camera.WorldMatrix;
            Vector3D from = cam.Translation, to = from + cam.Forward * RAY_LENGTH;
            var packet = new ScoutPointerPacket { Kind = kind, Mode = mode };
            var tool = character.EquippedTool as IMyAutomaticRifleGun;
            Vector3D hitPos, hitNormal;
            IMyEntity hitEntity;
            string how;
            if (RayHit(from, to, character, tool, out hitPos, out hitNormal, out hitEntity, out how))
            {
                var top = hitEntity != null ? hitEntity.GetTopMostParent() : null;
                packet.Hit = true;
                packet.HitPosition = Vector3DData.FromVector3D(hitPos);
                packet.HitEntityId = top != null ? top.EntityId : 0;
                packet.HitNormal = Vector3DData.FromVector3D(hitNormal);
                if (Log.DebugEnabled)
                    Log.Debug("Pointer: {0} {1} ({2}) fired: hit {3} at {4:F1} m ({9}), entity {5} '{6}' ({7}), relation {8}", kind, mode, ModeName(kind, mode),
                              hitPos, Vector3D.Distance(from, hitPos), packet.HitEntityId, top != null ? top.DisplayName : "-",
                              top != null ? top.GetType().Name : "-", Relation(top, character), how);
            }
            else
            {
                Log.Debug("Pointer: {0} {1} ({2}) fired: no hit within {3:F0} m", kind, mode, ModeName(kind, mode), RAY_LENGTH);
            }
            var net = AutomataSession.Instance != null ? AutomataSession.Instance.Net : null;
            if (net != null) net.SendToServer(packet);
        }

        private static readonly List<IHitInfo> rayHits = new List<IHitInfo>();

        // First hit along the ray that isn't the player or the pointer:
        //  1. physics (default layer: grids, voxels, characters) - only where physics exist, which for voxels is close
        //     to entities (a mountain a few km off has none);
        //  2. the planet's surface, ray-marched against its height map (any distance), if nearer;
        //  3. nothing yet: the long ray (asteroids beyond loaded physics).
        private static bool RayHit(Vector3D from, Vector3D to, IMyCharacter character, IMyEntity tool,
                                   out Vector3D pos, out Vector3D normal, out IMyEntity entity, out string how)
        {
            pos = normal = Vector3D.Zero;
            entity = null;
            how = null;
            rayHits.Clear();
            MyAPIGateway.Physics.CastRay(from, to, rayHits, 15);
            IHitInfo best = null;
            for (int i = 0; i < rayHits.Count; i++)
            {
                var h = rayHits[i];
                var e = h.HitEntity != null ? h.HitEntity.GetTopMostParent() : null;
                if (e != null && (e == character || e == tool)) continue;
                if (best == null || h.Fraction < best.Fraction) best = h;
            }
            rayHits.Clear();
            if (best != null) { pos = best.Position; normal = best.Normal; entity = best.HitEntity; how = "physics"; }
            Vector3D dir = Vector3D.Normalize(to - from);
            double limit = best != null ? Vector3D.Distance(from, best.Position) : Vector3D.Distance(from, to);
            Vector3D surfaceHit, surfaceNormal;
            IMyEntity planet;
            if (PlanetHit(from, dir, limit, out surfaceHit, out surfaceNormal, out planet))
            {
                pos = surfaceHit; normal = surfaceNormal; entity = planet; how = "planet surface";
                return true;
            }
            if (best != null) return true;
            IHitInfo longHit;
            if (MyAPIGateway.Physics.CastLongRay(from + dir, to, out longHit, false) && longHit != null)
            {
                pos = longHit.Position; normal = longHit.Normal; entity = longHit.HitEntity; how = "long ray";
                return true;
            }
            return false;
        }

        private const int PLANET_MARCH_STEPS = 600;

        // Marches the ray over the closest planet's height map: steps of half the height above the surface (at least
        // 1 m), then bisects the step that went below it. Normal: the local vertical (good enough for a standoff).
        private static bool PlanetHit(Vector3D from, Vector3D dir, double length, out Vector3D hit, out Vector3D normal, out IMyEntity planetEntity)
        {
            hit = normal = Vector3D.Zero;
            planetEntity = null;
            var planet = Sandbox.Game.Entities.MyGamePruningStructure.GetClosestPlanet(from);
            if (planet == null) return false;
            Vector3D c = planet.PositionComp.GetPosition();
            double prev = 0, t = 0;
            for (int step = 0; step < PLANET_MARCH_STEPS && t <= length; step++)
            {
                double h = HeightOver(planet, ref c, from + dir * t);
                if (h <= 0)
                {
                    double lo = prev, hi = t;
                    for (int k = 0; k < 16; k++)
                    {
                        double mid = (lo + hi) * 0.5;
                        if (HeightOver(planet, ref c, from + dir * mid) <= 0) hi = mid; else lo = mid;
                    }
                    hit = from + dir * hi;
                    normal = Vector3D.Normalize(hit - c);
                    planetEntity = planet;
                    return true;
                }
                prev = t;
                t += Math.Max(1.0, h * 0.5);
            }
            return false;
        }

        private static double HeightOver(Sandbox.Game.Entities.MyPlanet planet, ref Vector3D centre, Vector3D p)
        {
            Vector3D s = planet.GetClosestSurfacePointGlobal(ref p);
            return Vector3D.Distance(p, centre) - Vector3D.Distance(s, centre);
        }


        public static string ModeName(PointerKind kind, PointerMode mode)
        {
            if (mode == PointerMode.Tag) return "tag";
            if (mode == PointerMode.Green) return "recall";
            if (kind == PointerKind.Combat) return mode == PointerMode.Red ? "engage" : "escort";
            return mode == PointerMode.Red ? Loc.T(StrSpotCheck) : "area sweep";
        }

        /// <summary>The hit entity's relation to the player (grids: by majority owner; characters: their identity).</summary>
        public static string Relation(IMyEntity e, IMyCharacter character)
        {
            if (e == null) return "none";
            if (e is IMyVoxelBase) return "voxel";
            long me = character != null && MyAPIGateway.Session.Player != null ? MyAPIGateway.Session.Player.IdentityId : 0;
            long owner = 0;
            var grid = e as IMyCubeGrid;
            if (grid != null) owner = grid.BigOwners != null && grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0;
            var ch = e as IMyCharacter;
            if (ch != null)
            {
                var pl = PlayerOf(ch);
                owner = pl != null ? pl.IdentityId : 0;
            }
            if (owner == 0) return "unowned";
            if (owner == me) return "own";
            // Owner / faction (the mod's security rule) = friendly; anything else is treated as hostile here
            return Drone.AnchorDirectory.IsAllowed(me, owner) ? "faction" : "other";
        }

        #region Server: orders
        /// <summary>Drones a player can tag per mode (industrial: ore scouts; hostile: combat drones).</summary>
        public const int MAX_TAGGED = 32;
        #region Timing
        private const double TAG_MEMORY_MINUTES = 30;  // since the last tag / order with them
        #endregion
        /// <summary>Spot check by several tagged scouts: their spots this far apart around the pointed one.</summary>
        public const double SPOT_SPREAD = 25;

        // Per player: drones tagged with one designator (tag mode); kept through mode changes. Cleared on holster / swap
        // (client packet, or a pull with the other designator), when the order is given, after TAG_MEMORY_MINUTES, and on
        // reload (not saved). A failed tag changes nothing.
        private sealed class TagMemory
        {
            public PointerKind Kind;
            public readonly List<long> Drones = new List<long>();   // drone controller entity ids
            public DateTime Until;
        }
        private static readonly Dictionary<long, TagMemory> tags = new Dictionary<long, TagMemory>();

        /// <summary>Server: the player's client forgot its tags (holstered, swapped).</summary>
        public static void ClearTagsReceived(ulong steamId)
        {
            long identity = MyAPIGateway.Players.TryGetIdentityId(steamId);
            if (tags.Remove(identity)) Log.Debug("Pointer: [server] player {0}: tagged drones cleared", identity);
        }

        // The memory for this pull: the other designator's or expired -> forgotten (null)
        private static TagMemory TagsFor(long identity, PointerKind kind)
        {
            TagMemory m;
            if (!tags.TryGetValue(identity, out m))
                return null;
            if (m.Kind == kind && DateTime.UtcNow < m.Until)
                return m;
            tags.Remove(identity);
            SendTagCount(identity, kind, 0);
            return null;
        }

        // The drone a tag pull hit: on the hit grid group, the player's or their faction's, with the profile this designator
        // tags (hostile: combat drones; industrial: ore scouts). Null with why otherwise.
        private static Drone.DroneControllerBlock TagTarget(long entityId, long identity, PointerKind kind, out string why)
        {
            IMyEntity e;
            var grid = MyAPIGateway.Entities.TryGetEntityById(entityId, out e) ? e as IMyCubeGrid : null;
            why = "not a drone";
            if (grid == null) return null;
            var want = kind == PointerKind.Combat ? Drone.BehaviourProfile.IsCombatDrone : Drone.BehaviourProfile.IsOreScout;
            Drone.DroneControllerBlock other = null;
            bool foreign = false;
            DronesOn(grid);
            for (int i = 0; i < groupDrones.Count; i++)
            {
                var drone = groupDrones[i];
                if (!Drone.AnchorDirectory.IsAllowed(((IMyCubeBlock)drone.Entity).OwnerId, identity)) { foreign = true; continue; }
                if (drone.Profile == want) { groupDrones.Clear(); return drone; }
                other = drone;
            }
            groupDrones.Clear();
            if (other != null) why = string.Format("wrong designator: a {0}, this one tags {1}", other.Profile,
                                                   kind == PointerKind.Combat ? "combat drones" : "ore scouts");
            else if (foreign) why = "not the player's or their faction's";
            return null;
        }

        // Tagged drones still there (removed / closed ones dropped from the memory)
        private static List<long> LiveTags(TagMemory m)
        {
            if (m == null) return new List<long>();
            m.Drones.RemoveAll(id => { IMyEntity e; return !MyAPIGateway.Entities.TryGetEntityById(id, out e) || e == null || e.MarkedForClose; });
            return new List<long>(m.Drones);
        }

        // Drones (controllers) on a grid's mechanical group, into groupDrones
        private static readonly List<IMyCubeGrid> groupGrids = new List<IMyCubeGrid>();
        private static readonly List<Drone.DroneControllerBlock> groupDrones = new List<Drone.DroneControllerBlock>();
        private static void DronesOn(IMyCubeGrid grid)
        {
            groupDrones.Clear();
            groupGrids.Clear();
            MyAPIGateway.GridGroups.GetGroup(grid, GridLinkTypeEnum.Mechanical, groupGrids);
            for (int g = 0; g < groupGrids.Count; g++)
                foreach (var rc in groupGrids[g].GetFatBlocks<IMyRemoteControl>())
                {
                    var drone = rc.GameLogic != null ? rc.GameLogic.GetAs<Drone.DroneControllerBlock>() : null;
                    if (drone != null) groupDrones.Add(drone);
                }
            groupGrids.Clear();
        }

        // Online players by identity / Steam id / character (one shared list, no allocation per lookup)
        private static readonly List<IMyPlayer> playerBuffer = new List<IMyPlayer>();
        private static IMyPlayer FindPlayer(Func<IMyPlayer, bool> match)
        {
            playerBuffer.Clear();
            MyAPIGateway.Players.GetPlayers(playerBuffer);
            IMyPlayer found = null;
            for (int i = 0; i < playerBuffer.Count && found == null; i++) if (match(playerBuffer[i])) found = playerBuffer[i];
            playerBuffer.Clear();
            return found;
        }
        private static IMyPlayer PlayerOf(long identity) { return FindPlayer(p => p.IdentityId == identity); }
        private static IMyPlayer PlayerOf(IMyCharacter ch) { return FindPlayer(p => p.Character == ch); }

        /// <summary>
        /// Tag mode: the drone hit joins the memory (ok sound, the status lights blink green 3x). Refused (error sound, a red
        /// blink; the memory stays as it was): not a drone, the wrong designator for it (hostile: combat drones, industrial:
        /// ore scouts), another player's / faction's, already tagged, or MAX_TAGGED reached. No orchestrator needed (one in
        /// range broadcasts it).
        /// </summary>
        private static void TagAt(ScoutPointerPacket p, long identity, OrchestratorBlock orch)
        {
            string why = "no hit";
            var drone = p.Hit ? TagTarget(p.HitEntityId, identity, p.Kind, out why) : null;
            TagMemory m = TagsFor(identity, p.Kind);
            long id = drone != null ? drone.Entity.EntityId : 0;
            if (drone != null && m != null && m.Drones.Contains(id)) { why = "already tagged"; drone = null; }
            else if (drone != null && m != null && m.Drones.Count >= MAX_TAGGED) { why = "memory full (" + MAX_TAGGED + ")"; drone = null; }
            if (drone == null)
            {
                Log.Debug("Pointer: [server] player {0}: tag refused: {1}", identity, why);
                Feedback(identity, DesignatorSignal.Error);
                return;
            }
            if (m == null) { m = new TagMemory { Kind = p.Kind }; tags[identity] = m; }
            m.Drones.Add(id);
            m.Until = DateTime.UtcNow.AddMinutes(TAG_MEMORY_MINUTES);
            string name = ((IMyTerminalBlock)drone.Entity).CustomName;
            if (orch != null) orch.BroadcastTagged(name, p.Kind == PointerKind.Combat ? Loc.T(StrHostile) : Loc.T(StrIndustrial));
            Log.Info("Pointer: [server] player {0}: tagged drone {1} '{2}' ({3}/{4})", identity, id, name, m.Drones.Count, MAX_TAGGED);
            SendTagCount(identity, p.Kind, m.Drones.Count);
            Feedback(identity, DesignatorSignal.TagOk);
        }

        private static void SendTagCount(long identity, PointerKind kind, int count)
        {
            if (identity == 0) return;
            var local = MyAPIGateway.Session != null ? MyAPIGateway.Session.Player : null;
            if (local != null && local.IdentityId == identity) { TagCountReceived(kind, count); return; }
            var player = PlayerOf(identity);
            var net = AutomataSession.Instance != null ? AutomataSession.Instance.Net : null;
            if (player != null && net != null) net.SendToPlayer(new ScoutPointerTagsPacket { Kind = kind, Count = count }, player.SteamUserId);
        }

        // Spots for n tagged scouts: the pointed one, the others on rings around it in the surface's plane
        private static List<Vector3D> SpreadSpots(Vector3D hit, Vector3D normal, int n)
        {
            var spots = new List<Vector3D> { hit };
            if (n <= 1) return spots;
            Vector3D up = normal.LengthSquared() > 0.5 ? Vector3D.Normalize(normal) : Vector3D.Up;
            Vector3D u = Vector3D.CalculatePerpendicularVector(up), v = Vector3D.Cross(up, u);
            for (int k = 1; k < n; k++)
            {
                int ring = 1 + (k - 1) / 6;
                double ang = (k - 1) % 6 * Math.PI / 3 + ring * 0.5;
                spots.Add(hit + (u * Math.Cos(ang) + v * Math.Sin(ang)) * SPOT_SPREAD * ring);
            }
            return spots;
        }

        /// <summary>Server: a pull from a player: range check, then the order of its mode.</summary>
        public static void Received(ScoutPointerPacket p, ulong steamId)
        {
            long identity = MyAPIGateway.Players.TryGetIdentityId(steamId);
            Log.Debug("Pointer: [server] player {0} ({1}) {2} {3} ({4}): {5}", identity, steamId, p.Kind, p.Mode, ModeName(p.Kind, p.Mode),
                      p.Hit ? string.Format("hit {0}, entity {1}", p.HitPosition.ToVector3D(), p.HitEntityId) : "no hit");
            var player = FindPlayer(pl => pl.SteamUserId == steamId);
            if (player == null || player.Character == null) return;
            Vector3D at = player.Character.GetPosition();
            // (Suit broadcasting isn't checked: the mod API has no reliable read of it)
            OrchestratorBlock orch = null;
            string orchVia = null;
            double best = double.MaxValue;
            for (int i = 0; i < OrchestratorBlock.All.Count; i++)
            {
                var o = OrchestratorBlock.All[i];
                string via;
                if (!o.IsRunning || !Drone.AnchorDirectory.IsAllowed(o.OwnerId, identity) || !o.Covers(at, identity, out via)) continue;
                double d = Vector3D.DistanceSquared(o.Entity.GetPosition(), at);
                if (d < best) { best = d; orch = o; orchVia = via; }
            }
            if (orch != null) Log.Debug("Pointer: [server] player {0}: in range of orchestrator {1} via antenna '{2}'", identity, orch.Entity.EntityId, orchVia);
            if (p.Mode == PointerMode.Tag) { TagAt(p, identity, orch); return; }
            if (orch == null)
            {
                Feedback(identity, DesignatorSignal.Error);
                Log.Debug("Pointer: [server] player {0}: no orchestrator of theirs covers {1}", identity, at);
                return;
            }
            if (!p.Hit) { Feedback(identity, DesignatorSignal.Error); return; }
            Vector3D hit = p.HitPosition.ToVector3D();
            double dist = Vector3D.Distance(at, hit);
            if (p.Mode == PointerMode.Green) { Recall(p.HitEntityId, hit, identity); return; }
            // Orders: the tagged drones (tag mode) take them, one job each; none tagged: one drone of the profile
            var memory = TagsFor(identity, p.Kind);
            var tagged = LiveTags(memory);   // empty: one drone, any of the profile (as before tagging)
            if (memory != null) memory.Until = DateTime.UtcNow.AddMinutes(TAG_MEMORY_MINUTES);
            if (p.Kind == PointerKind.Combat)
            {
                if (CombatOrder(p, identity, dist, orch, player.Character, tagged))
                {
                    Feedback(identity, DesignatorSignal.Requested);
                    if (tagged.Count > 0) ForgetTags(identity, p.Kind, "order given");
                }
                return;
            }
            uint order = 0;
            if (p.Mode == PointerMode.Red)
            {
                // Spot check: a yellow marker now, green once the orchestrator confirms (OrchestratorBlock.PlayerJobs);
                // tagged scouts: one spot each around the pointed one
                var spots = SpreadSpots(hit, p.HitNormal.ToVector3D(), Math.Max(1, tagged.Count));
                for (int s = 0; s < spots.Count; s++)
                    orch.SubmitPlayerJob(Orchestrator.JobType.DesignatorScoutPoint, ScoutPlanner.SpotCheck(spots[s], p.HitNormal.ToVector3D(), at), hit,
                                         spots.Count > 1 ? Loc.F(StrSpotCheckN, s + 1, spots.Count) : Loc.T(StrSpotCheck), identity, p.Kind,
                                         Loc.T(StrSpotCheckMarker), ref order, s < tagged.Count ? tagged[s] : 0);
                Feedback(identity, DesignatorSignal.Requested);
                if (tagged.Count > 0) ForgetTags(identity, p.Kind, "order given");
                return;
            }
            // Blue: an area sweep, one sector per tagged scout (none tagged: one drone)
            string how;
            var sectors = ScoutPlanner.AreaSweep(hit, at, Math.Max(1, tagged.Count), out how, SmallestDetectorRange(tagged));
            for (int s = 0; s < sectors.Count; s++)
                orch.SubmitPlayerJob(Orchestrator.JobType.DesignatorScoutArea, sectors[s], hit, Loc.F(StrAreaSweepSector, s + 1, sectors.Count),
                                     identity, p.Kind, Loc.T(StrAreaSweep), ref order, s < tagged.Count ? tagged[s] : 0);
            Log.Info("Pointer: [server] area sweep at {0} for player {1}: {2}", hit, identity, how);
            Feedback(identity, DesignatorSignal.Requested);
            if (tagged.Count > 0) ForgetTags(identity, p.Kind, "order given");
        }

        // The smallest ore detector range among the tagged scouts (0: none tagged / none readable)
        private static double SmallestDetectorRange(List<long> tagged)
        {
            double best = 0;
            for (int i = 0; i < tagged.Count; i++)
            {
                IMyEntity e;
                var tb = MyAPIGateway.Entities.TryGetEntityById(tagged[i], out e) ? e as IMyTerminalBlock : null;
                var d = tb != null && tb.GameLogic != null ? tb.GameLogic.GetAs<Drone.DroneControllerBlock>() : null;
                double r = d != null ? d.OreDetectorRangeMeters : 0;
                if (r > 0 && (best == 0 || r < best)) best = r;
            }
            return best;
        }

        // The tagged drones were given their order (or the memory is otherwise done with): forgotten, the counter back to 0
        private static void ForgetTags(long identity, PointerKind kind, string why)
        {
            if (!tags.Remove(identity)) return;
            SendTagCount(identity, kind, 0);
            Log.Debug("Pointer: [server] player {0}: tagged drones forgotten ({1})", identity, why);
        }

        /// <summary>Hostile designator, blue: a hit this close to the player escorts the player (or the mobile grid they are on).</summary>
        public const double SELF_ESCORT_RADIUS = 5;

        // Hostile designator: the target is checked at once, then ordered at the orchestrator like the scout jobs
        // (OrchestratorBlock.PlayerJobs: combat drones only, confirmed within ConfirmTimeoutSeconds or cancelled).
        //  - Red (engage): a hostile or neutral (unowned, other owner / faction, NPC) grid or character.
        //  - Blue (escort): a non-static grid or a character owned by the player or their faction. A hit within
        //    SELF_ESCORT_RADIUS of the player (ground, a static grid, the grid they stand on) escorts the player: the mobile
        //    friendly grid that was hit (escorting the base they are on escorts them), else the player's character.
        //  - Tagged combat drones (this mode): one job per drone, each only for its drone; none tagged: one job, any combat
        //    drone (each further pull sends one more, as before).
        private static bool CombatOrder(ScoutPointerPacket p, long identity, double dist, OrchestratorBlock orch, IMyCharacter shooter, List<long> tagged)
        {
            int n = Math.Max(1, tagged.Count);
            IMyEntity e;
            var target = MyAPIGateway.Entities.TryGetEntityById(p.HitEntityId, out e) ? e : null;
            if (p.Mode == PointerMode.Blue && dist <= SELF_ESCORT_RADIUS && shooter != null && !(target is IMyCharacter))
            {
                var standing = target as IMyCubeGrid;
                bool mobileBase = standing != null && !standing.IsStatic && IsFriendlyGrid(standing, identity);
                target = mobileBase ? (IMyEntity)standing : shooter;
                Log.Debug("Pointer: [server] player {0}: escort request on themselves ({1:F1} m): {2}", identity, dist,
                          mobileBase ? "the mobile grid they are on, '" + standing.DisplayName + "'" : "their character");
            }
            var grid = target as IMyCubeGrid;
            var ch = target as IMyCharacter;
            if (grid == null && ch == null)
            {
                // Engage at a point (terrain, an asteroid): the drone flies there and the AI fights whatever it finds
                if (p.Mode != PointerMode.Red) { Feedback(identity, DesignatorSignal.Error); return false; }
                Vector3D spot = p.HitPosition.ToVector3D();
                uint pointOrder = 0;
                for (int i = 0; i < n; i++)
                    orch.SubmitPlayerJob(Orchestrator.JobType.DesignatorEngage, new List<Orchestrator.Task> { Orchestrator.Task.Engage(0, spot) }, spot,
                                         Loc.T(StrEngageAtAPoint), identity, p.Kind, Loc.T(StrADesignatedPoint), ref pointOrder, i < tagged.Count ? tagged[i] : 0);
                return true;
            }
            long owner = 0;
            bool npc = false;
            if (grid != null) owner = grid.BigOwners != null && grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0;
            else
            {
                var pl = PlayerOf(ch);
                if (pl != null) owner = pl.IdentityId; else npc = true;   // wildlife, bots
            }
            bool friendly = owner != 0 && Drone.AnchorDirectory.IsAllowed(identity, owner);   // the player or their faction
            string name = grid != null ? grid.DisplayName : ch.DisplayName;
            Vector3D at = grid != null ? grid.WorldAABB.Center : ch.WorldAABB.Center;
            uint order = 0;
            if (p.Mode == PointerMode.Red)
            {
                if (friendly && !npc) { Feedback(identity, DesignatorSignal.Error); return false; }
                // The drone is dispatched where the designator hit (its GPS in the drone's broadcast); it tracks the entity
                Vector3D hit = p.HitPosition.ToVector3D();
                for (int i = 0; i < n; i++)
                    orch.SubmitPlayerJob(Orchestrator.JobType.DesignatorEngage, new List<Orchestrator.Task> { Orchestrator.Task.Engage(target.EntityId, hit) },
                                         hit, Loc.F(StrEngageTarget, name), identity, p.Kind, name, ref order, i < tagged.Count ? tagged[i] : 0);
                return true;
            }
            if (!friendly || npc || (grid != null && grid.IsStatic)) { Feedback(identity, DesignatorSignal.Error); return false; }
            for (int i = 0; i < n; i++)
                orch.SubmitPlayerJob(Orchestrator.JobType.DesignatorEscort, new List<Orchestrator.Task> { Orchestrator.Task.Escort(target.EntityId, at) },
                                     at, Loc.F(StrEscortTarget, name), identity, p.Kind, name, ref order, i < tagged.Count ? tagged[i] : 0);
            return true;
        }

        private static bool IsFriendlyGrid(IMyCubeGrid grid, long identity)
        {
            long owner = grid.BigOwners != null && grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0;
            return owner != 0 && Drone.AnchorDirectory.IsAllowed(identity, owner);
        }

        private static readonly List<long> tagsExpired = new List<long>();
        private static DateTime nextTagCheck;

        /// <summary>Server, every tick: tag memories past TAG_MEMORY_MINUTES are forgotten (the player's light goes off).</summary>
        public static void UpdateServer()
        {
            if (tags.Count == 0) return;
            DateTime now = DateTime.UtcNow;
            if (now < nextTagCheck) return;
            nextTagCheck = now.AddSeconds(10);
            tagsExpired.Clear();
            foreach (var kv in tags) if (now >= kv.Value.Until) tagsExpired.Add(kv.Key);
            for (int i = 0; i < tagsExpired.Count; i++)
            {
                var kind = tags[tagsExpired[i]].Kind;
                tags.Remove(tagsExpired[i]);
                SendTagCount(tagsExpired[i], kind, 0);
                Log.Debug("Pointer: [server] player {0}: tagged drones expired", tagsExpired[i]);
            }
            tagsExpired.Clear();
        }

        private const double RECALL_MARKER_RADIUS = 8;   // m: a hit this close to an order's GPS marker recalls that order

        // Green: the pointed drone (the player's / faction's) comes home; else the order whose marker is pointed at (its
        // drones come home, jobs not yet taken are cancelled)
        private static void Recall(long entityId, Vector3D hit, long identity)
        {
            IMyEntity e;
            var grid = MyAPIGateway.Entities.TryGetEntityById(entityId, out e) ? e as IMyCubeGrid : null;
            int n = 0;
            if (grid != null)
            {
                DronesOn(grid);
                for (int i = 0; i < groupDrones.Count; i++)
                    if (Drone.AnchorDirectory.IsAllowed(((IMyCubeBlock)groupDrones[i].Entity).OwnerId, identity) && groupDrones[i].Recall(Loc.T(StrScoutLaser))) n++;
                groupDrones.Clear();
            }
            if (n == 0)
            {
                for (int i = 0; i < OrchestratorBlock.All.Count && n == 0; i++)
                {
                    var o = OrchestratorBlock.All[i];
                    int drones, cancelled;
                    if (!o.IsRunning || !Drone.AnchorDirectory.IsAllowed(o.OwnerId, identity)) continue;
                    if (o.RecallOrderAt(hit, RECALL_MARKER_RADIUS, Loc.T(StrScoutLaser), out drones, out cancelled)) n = drones + cancelled;
                }
            }
            Feedback(identity, n > 0 ? DesignatorSignal.Recall : DesignatorSignal.Error);
        }

        /// <summary>Server: the firing player's feedback (DesignatorSignal: status light blinks and a sound).</summary>
        public static void Feedback(long identity, DesignatorSignal signal)
        {
            if (identity == 0) return;
            var local = MyAPIGateway.Session != null ? MyAPIGateway.Session.Player : null;
            if (local != null && local.IdentityId == identity) { FeedbackReceived(signal); return; }   // the host / single player
            var player = PlayerOf(identity);
            var net = AutomataSession.Instance != null ? AutomataSession.Instance.Net : null;
            if (player != null && net != null) net.SendToPlayer(new ScoutPointerFeedbackPacket { Signal = signal }, player.SteamUserId);
        }

        /// <summary>Client: the server's answer: the held designator's status lights blink; the ok / request / error sound.
        /// An error cuts a charge sound still playing, so it is as short as tag mode's.</summary>
        public static void FeedbackReceived(DesignatorSignal signal)
        {
            var held = HeldDesignator();
            if (signal == DesignatorSignal.Error && held != null) DesignatorVisuals.StopChargeSound(held);
            DesignatorVisuals.PlayFeedback(signal);
            if (held != null) DesignatorVisuals.StartBlink(held, signal);
        }
        #endregion
    }

    /// <summary>A pointer pull, client -> server.</summary>
    [ProtoContract]
    public class ScoutPointerPacket : PacketBase
    {
        public ScoutPointerPacket() { }

        [ProtoMember(1)] public PointerKind Kind;
        [ProtoMember(2)] public PointerMode Mode;
        [ProtoMember(5)] public bool Hit;
        [ProtoMember(6)] public Vector3DData HitPosition;
        [ProtoMember(7)] public long HitEntityId;
        [ProtoMember(8)] public Vector3DData HitNormal;

        public override void Received(ref PacketInfo packetInfo, ulong senderSteamId)
        {
            ScoutPointer.Received(this, senderSteamId);
        }
    }

    /// <summary>Client -> server: the player's tag memory is forgotten (mode change, holstered, swapped).</summary>
    [ProtoContract]
    public class ScoutPointerClearTagsPacket : PacketBase
    {
        public ScoutPointerClearTagsPacket() { }

        public override void Received(ref PacketInfo packetInfo, ulong senderSteamId)
        {
            ScoutPointer.ClearTagsReceived(senderSteamId);
        }
    }

    /// <summary>Server -> the player: how many drones are tagged (0: memory empty), for the designator's light.</summary>
    [ProtoContract]
    public class ScoutPointerTagsPacket : PacketBase
    {
        public ScoutPointerTagsPacket() { }

        [ProtoMember(1)] public PointerKind Kind;
        [ProtoMember(2)] public int Count;

        public override void Received(ref PacketInfo packetInfo, ulong senderSteamId)
        {
            ScoutPointer.TagCountReceived(Kind, Count);
        }
    }

    /// <summary>The server's answer to a pull / an order's outcome, server -> the firing player (DesignatorSignal).</summary>
    [ProtoContract]
    public class ScoutPointerFeedbackPacket : PacketBase
    {
        public ScoutPointerFeedbackPacket() { }

        [ProtoMember(3)] public DesignatorSignal Signal;

        public override void Received(ref PacketInfo packetInfo, ulong senderSteamId)
        {
            ScoutPointer.FeedbackReceived(Signal);
        }
    }
}
