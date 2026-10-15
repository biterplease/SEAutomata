using System;
using System.Collections.Generic;

using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Weapons;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Util.Logging;

namespace Automata.Pointer
{
    /// <summary>
    /// Client-side looks of the target designators (no gameplay): the tuner knob turning with the Reload animation, the
    /// tagged-drone counter wheels, emissive lights (mode, charge sequence, signal blinks) and the sounds.
    ///
    /// Moving parts (knob, counter wheels): subparts of the designator models. The game only builds subparts for
    /// blocks, so for a hand item they are spawned here (entities without physics, placed on their dummies every
    /// frame); if the game did build one, that one is used instead. A part's model is its dummy's "file" property
    /// (Models\Items): the knob is the dummy whose file is KNOB_FILE_PREFIX + the designator's subtype
    /// (Subpart_AutomataHostileTargetDesignator / Subpart_AutomataIndustrialTargetDesignator), the wheels the dummies
    /// named with COUNTER_DUMMIES.
    ///
    /// Lights: emissive materials of the designator model, driven with SetEmissiveParts. That is what a block's
    /// emissive "state" is: SetEmissiveState(Working / Disabled / ...) looks the state's color and emissivity up in
    /// Data/EmissiveColors.sbc and sets them on the block's "Emissive" material. Hand items have no state presets, so
    /// the color / emissivity are set directly. One material changes all its faces at once:
    ///  - MODE_LIGHTS: one material per mode; the current mode's lamp is lit in the mode's color;
    ///  - CHARGE_LIGHTS: one material per lamp, lit in order over the charge: green, the last one in the mode's color
    ///    (CHARGE_LAST_INDUSTRIAL / CHARGE_LAST_HOSTILE) (batteries do their charge bars this way, one material per bar);
    ///    the same lamps blink the server's signals (DesignatorSignal);
    ///  - CHARGE_GLOW: optional, one material whose glow ramps up 0 -> 1 over the charge;
    ///  - TAG_LIGHT: lit while the player has drones tagged.
    /// Unlit lamps get OFF_COLOR / OFF_EMISSIVITY (the material's own look as far as possible). A name the model
    /// doesn't have is simply ignored.
    ///
    /// Local only: other players see the knob at its rest angle, no lights, and don't hear the charge.
    /// </summary>
    public static class DesignatorVisuals
    {
        #region Tuning (adjust here)
        /// <summary>Frame rate of the Blender animations the frames below come from.</summary>
        public const float ANIMATION_FPS = 30f;
        /// <summary>The animation's first frame in Blender (it plays from here when Reload is pressed).</summary>
        public const int ANIMATION_START_FRAME = 1;
        /// <summary>Frame of the Reload animation at which the hand grabs the knob (it starts turning).</summary>
        public const int TUNER_GRAB_FRAME = 18;
        /// <summary>Frame at which the hand is done adjusting (the knob stops turning).</summary>
        public const int TUNER_RELEASE_FRAME = 38;
        /// <summary>The animation's last frame: until then Reload and the trigger are ignored (IsTuning).</summary>
        public const int ANIMATION_END_FRAME = 50;
        /// <summary>Knob turn per mode change, degrees. Negative turns the other way.</summary>
        public const float TUNER_DEGREES_PER_MODE = -90f;   // four modes (tag, two orders, recall)
        /// <summary>The knob's rotation axis, in the subpart's own space (its dummy's axes).</summary>
        public static readonly Vector3 TUNER_AXIS = Vector3.Right;
        /// <summary>The point the knob turns about, in the subpart's own space. null: the centre of the knob model's
        /// bounding box (the mesh needn't sit on the subpart's origin).</summary>
        public static readonly Vector3? TUNER_PIVOT = null;
        /// <summary>The knob's model (its dummy's "file"): this + the designator's subtype.</summary>
        public const string KNOB_FILE_PREFIX = "Subpart_";

        /// <summary>Mode lamps, one per mode (red, blue, green): the current mode's lamp is lit in its color.</summary>
        public static readonly string[] MODE_LIGHTS = { "EmissiveMode0", "EmissiveMode1", "EmissiveMode2" };   // Red, Blue, Green; tag mode: all three teal
        /// <summary>Charge lamps, lit one after the other over the charge.</summary>
        public static readonly string[] CHARGE_LIGHTS = { "Emissive0", "Emissive1", "Emissive2", "Emissive3", "Emissive4" };
        /// <summary>Lit charge lamps; the last one shows the mode (ChargeLastColor).</summary>
        public static readonly Color CHARGE_COLOR = new Color(40, 255, 60);
        /// <summary>Last charge lamp per mode (Red, Blue, Green, Tag), industrial: spot check, area sweep, recall, tag.</summary>
        public static readonly Color[] CHARGE_LAST_INDUSTRIAL = { new Color(255, 210, 0), new Color(40, 120, 255), new Color(10, 120, 25), new Color(0, 200, 180) };
        /// <summary>Last charge lamp per mode (Red, Blue, Green, Tag), hostile: engage, escort, recall, tag.</summary>
        public static readonly Color[] CHARGE_LAST_HOSTILE = { new Color(255, 30, 20), new Color(40, 120, 255), new Color(10, 120, 25), new Color(0, 200, 180) };
        /// <summary>Unlit lamps: no glow, white so the material keeps its own color.</summary>
        public static readonly Color OFF_COLOR = Color.White;
        public const float OFF_EMISSIVITY = 0f;
        /// <summary>Optional: one material whose glow ramps up over the charge (CHARGE_COLOR).</summary>
        public const string CHARGE_GLOW = "EmissiveCharge";
        /// <summary>Glow steps of CHARGE_GLOW over the charge (each step is one render message).</summary>
        public const int CHARGE_GLOW_STEPS = 20;
        public const float LIGHT_EMISSIVITY = 1f;
        /// <summary>Lit (TAG_COLOR) while the player has drones tagged with this designator: the model's "Emissive" material.</summary>
        public const string TAG_LIGHT = "Emissive";
        public static readonly Color TAG_COLOR = new Color(0, 200, 180);   // teal
        public const string CHARGE_SOUND = "AutomataDesignatorCharge";   // Data/Audio.sbc (Arc / Real)
        /// <summary>Order confirmed, tag / recall accepted: three chirps.</summary>
        public const string OK_SOUND = "AutomataDesignatorOk";
        public const string ERROR_SOUND = "AutomataDesignatorError";
        /// <summary>Order sent: the ok sound's first chirp (AutomataDesignatorRequest.wav, 0.1 s).</summary>
        public const string REQUEST_SOUND = "AutomataDesignatorRequest";
        #endregion

        /// <summary>A turning subpart (knob, counter wheel): its entity, rest matrix on the designator, pivot / axis, angle.</summary>
        private sealed class Part
        {
            public MyEntity Entity;
            public bool Own;               // spawned here: placed in world space every frame
            public Matrix Base;            // rest matrix relative to the designator
            public Vector3 Pivot, Axis;    // in its own space
            public float Angle, From, To;  // degrees, accumulated
            public double TurnStart = -1;  // play time the turn starts (may lie ahead: the knob waits for the hand); -1: still
            public double TurnSeconds;
            public bool Placed;            // the game's subpart: its local matrix is up to date
        }

        private sealed class Visual
        {
            public IMyAutomaticRifleGun Rifle;
            public bool PartsTried;
            public readonly Part Knob = new Part { Axis = TUNER_AXIS };
            public Part[] Counters;        // units, tens (entries null: not on the model)
            public int CounterShown;       // the count the wheels show / turn to
            public double TuneEnd;         // play time the Reload animation ends
            public int LitCharge = -1;
            public int GlowStep = -1;
            public int ModeShown = -1;
            public int TagShown = -1;      // 1: the tag light on, 0: off
            public double BlinkStart = -1; // a signal on the status (charge) lights; -1: none
            public double[] BlinkWindows;
            public Color BlinkColor;
            public int BlinkLit = -1;      // the lights' blink state shown: 1 lit, 0 off
            public MyEntity3DSoundEmitter Emitter;
        }

        private static readonly Dictionary<long, Visual> visuals = new Dictionary<long, Visual>();
        private static readonly List<long> gone = new List<long>();
        private static readonly Dictionary<string, IMyModelDummy> dummies = new Dictionary<string, IMyModelDummy>();
        private static string modPath;
        private static MySoundPair chargeSound, okSound, errorSound, requestSound;
        private static MyEntity3DSoundEmitter feedbackEmitter;   // 2D, at the camera (as PaintGun's HUD sounds)

        public static void Load(string path)
        {
            modPath = path;
        }

        public static void Unload()
        {
            foreach (var v in visuals.Values) Dispose(v);
            visuals.Clear();
            chargeSound = okSound = errorSound = requestSound = null;
            if (feedbackEmitter != null) { feedbackEmitter.StopSound(true); feedbackEmitter.Cleanup(); feedbackEmitter = null; }
        }

        private static double Now
        {
            get { return MyAPIGateway.Session != null ? MyAPIGateway.Session.ElapsedPlayTime.TotalSeconds : 0; }
        }

        private static Visual Get(IMyAutomaticRifleGun rifle)
        {
            Visual v;
            if (visuals.TryGetValue(rifle.EntityId, out v)) return v;
            v = new Visual { Rifle = rifle };
            visuals[rifle.EntityId] = v;
            return v;
        }

        private static void Dispose(Visual v)
        {
            Close(v.Knob);
            if (v.Counters != null) for (int i = 0; i < v.Counters.Length; i++) Close(v.Counters[i]);
            v.Counters = null;
            if (v.Emitter != null) { v.Emitter.StopSound(true); v.Emitter.Cleanup(); v.Emitter = null; }
        }

        /// <summary>
        /// Clients, every frame (session Draw): parts attached once in the scene, mode lamps, the tag light and counter
        /// (the held designator's 'heldTags', 0 for the rest), knobs / wheels turned and placed, blinks; closed designators
        /// cleaned up. 'heldMode': the held designator's mode; others show the first mode.
        /// </summary>
        public static void Draw(List<IMyAutomaticRifleGun> rifles, IMyAutomaticRifleGun held, PointerMode heldMode, int heldTags)
        {
            for (int i = 0; i < rifles.Count; i++)
            {
                var r = rifles[i];
                if (r == null || r.MarkedForClose || r.Closed) continue;
                var v = Get(r);
                var ent = (MyEntity)r;
                if (!v.PartsTried && ent.InScene && r.Model != null) InitParts(v, r == held ? heldMode : ScoutPointer.MODE_CYCLE[0]);
                ShowMode(v, r == held ? heldMode : ScoutPointer.MODE_CYCLE[0]);
                int tags = r == held ? heldTags : 0;
                ShowTagged(v, tags > 0);
                ShowCounter(v, tags);
                UpdatePart(v.Knob, ent);
                if (v.Counters != null) for (int c = 0; c < v.Counters.Length; c++) UpdatePart(v.Counters[c], ent);
                UpdateBlink(v);
            }
            gone.Clear();
            foreach (var kv in visuals)
            {
                var r = kv.Value.Rifle;
                if (r == null || r.MarkedForClose || r.Closed) gone.Add(kv.Key);
            }
            for (int i = 0; i < gone.Count; i++)
            {
                Dispose(visuals[gone[i]]);
                visuals.Remove(gone[i]);
            }
        }

        #region Moving parts (knob, counter wheels)
        /// <summary>Tagged-drone counter: two 10-faced wheels (digits 0-9). Dummies whose name contains these.</summary>
        public static readonly string[] COUNTER_DUMMIES = { "counter_unit", "counter_tens" };
        /// <summary>Degrees per digit. Negative turns the other way (if the digits run backwards).</summary>
        public const float COUNTER_DEGREES_PER_DIGIT = 36f;
        /// <summary>The wheels' axis in their own space. null: the model's odd bounding-box dimension (a cylinder's length).</summary>
        public static readonly Vector3? COUNTER_AXIS = null;
        /// <summary>Seconds a wheel takes to turn to its new digit.</summary>
        public const double COUNTER_TURN_SECONDS = 0.3;

        // Once, in the scene: the knob and the wheels on the model's dummies, at the angles they should show already
        private static void InitParts(Visual v, PointerMode mode)
        {
            v.PartsTried = true;
            var ent = (MyEntity)v.Rifle;
            if (modPath == null) return;
            if (v.Knob.TurnStart < 0) v.Knob.Angle = ScoutPointer.ModeIndex(mode) * TUNER_DEGREES_PER_MODE;
            v.Counters = new Part[COUNTER_DUMMIES.Length];
            string knobFile = KNOB_FILE_PREFIX + v.Rifle.DefinitionId.SubtypeName;
            dummies.Clear();
            v.Rifle.Model.GetDummies(dummies);
            foreach (var kv in dummies)
            {
                string file = FileOf(kv.Value);
                if (file != null && string.Equals(file, knobFile, StringComparison.OrdinalIgnoreCase))
                {
                    if (Attach(v.Knob, ent, kv.Key, kv.Value, file)) v.Knob.Pivot = TUNER_PIVOT ?? v.Knob.Pivot;
                    continue;
                }
                for (int i = 0; i < COUNTER_DUMMIES.Length; i++)
                {
                    if (v.Counters[i] != null || kv.Key.IndexOf(COUNTER_DUMMIES[i], StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (file == null) { if (Log.DebugEnabled) Log.Debug("Pointer: counter dummy {0} has no file", kv.Key); break; }
                    var c = new Part();
                    if (!Attach(c, ent, kv.Key, kv.Value, file)) break;
                    c.Axis = COUNTER_AXIS ?? CylinderAxis(c.Entity.PositionComp.LocalAABB.Size);
                    c.Angle = c.To = CounterAngle(i, v.CounterShown);
                    v.Counters[i] = c;
                    break;
                }
            }
            dummies.Clear();
            if (v.Knob.Entity == null) if (Log.DebugEnabled) Log.Debug("Pointer: no knob dummy (file {0}) on {1}", knobFile, v.Rifle.DefinitionId.SubtypeName);
        }

        private static string FileOf(IMyModelDummy dummy)
        {
            object file;
            return dummy.CustomData != null && dummy.CustomData.TryGetValue("file", out file) && file is string && ((string)file).Length > 0
                ? (string)file : null;
        }

        // The game's subpart for this dummy, else our own entity with the dummy's model
        private static bool Attach(Part p, MyEntity rifle, string dummyName, IMyModelDummy dummy, string file)
        {
            MyEntitySubpart sp;
            string name = dummyName.StartsWith("subpart_", StringComparison.OrdinalIgnoreCase) ? dummyName.Substring(8) : dummyName;
            if (rifle.TryGetSubpart(name, out sp) && sp != null)
            {
                p.Entity = sp;
                p.Base =  sp.PositionComp.LocalMatrixRef;
            }
            else
            {
                string model = @"Models\Items\" + file + ".mwm";
                try
                {
                    var e = new MyEntity();
                    e.Init(null, modPath + "\\" + model, null, null, null);
                    e.Save = false;
                    e.Flags |= EntityFlags.IsNotGamePrunningStructureObject;
                    MyEntities.Add(e, true);
                    p.Entity = e;
                    p.Own = true;
                    p.Base = Matrix.Normalize(dummy.Matrix);
                }
                catch (Exception ex)
                {
                    Log.Error("Pointer: part {0} ({1}): {2}", dummyName, model, ex.Message);
                    return false;
                }
            }
            p.Pivot = p.Entity.PositionComp.LocalAABB.Center;
            if (Log.DebugEnabled) Log.Debug("Pointer: part {0} on {1}: {2}, box {3}", dummyName, rifle.DefinitionId != null ? rifle.DefinitionId.Value.SubtypeName : "?",
                      p.Own ? @"own entity, Models\Items\" + file + ".mwm" : "the game's subpart", p.Entity.PositionComp.LocalAABB.Size);
            return true;
        }

        private static void Close(Part p)
        {
            if (p != null && p.Own && p.Entity != null && !p.Entity.MarkedForClose) p.Entity.Close();
            if (p != null) p.Entity = null;
        }

        // Turned (ease in / out) and placed: our own entities every frame (they follow the designator), the game's
        // subparts only while they turn
        private static void UpdatePart(Part p, MyEntity rifle)
        {
            if (p == null || p.Entity == null || p.Entity.MarkedForClose) return;
            bool turning = p.TurnStart >= 0;
            if (turning)
            {
                double t = (Now - p.TurnStart) / p.TurnSeconds;
                if (t >= 1) { p.Angle = p.To; p.TurnStart = -1; }
                else if (t > 0) { float e = (float)t; e = e * e * (3f - 2f * e); p.Angle = p.From + (p.To - p.From) * e; }
                else p.Angle = p.From;
            }
            if (!p.Own && !turning && p.Placed) return;
            Matrix local = Matrix.CreateTranslation(-p.Pivot) * Matrix.CreateFromAxisAngle(p.Axis, MathHelper.ToRadians(p.Angle))
                         * Matrix.CreateTranslation(p.Pivot) * p.Base;
            if (p.Own)
            {
                ((IMyEntity)p.Entity).Visible = ((IMyEntity)rifle).Visible;
                p.Entity.WorldMatrix = (MatrixD)local * rifle.WorldMatrix;
            }
            else
            {
                p.Entity.PositionComp.SetLocalMatrix(ref local);
                p.Placed = p.TurnStart < 0;
            }
        }

        private static void Turn(Part p, float to, double delay, double seconds)
        {
            p.From = p.Angle;
            p.To = to;
            p.TurnStart = Now + delay;
            p.TurnSeconds = seconds;
        }

        // A 10-faced cylinder's box: two sizes about equal (across the faces), the third its length - the axis
        private static Vector3 CylinderAxis(Vector3 s)
        {
            float dx = Math.Abs(s.X - (s.Y + s.Z) * 0.5f), dy = Math.Abs(s.Y - (s.X + s.Z) * 0.5f), dz = Math.Abs(s.Z - (s.X + s.Y) * 0.5f);
            return dx >= dy && dx >= dz ? Vector3.Right : dy >= dz ? Vector3.Up : Vector3.Backward;
        }

        // Units keep turning forward past 9 (one face per drone); tens one face per ten
        private static float CounterAngle(int wheel, int count)
        {
            return (wheel == 0 ? count : count / 10) * COUNTER_DEGREES_PER_DIGIT;
        }

        // The wheels turn to the count's digits (0 = reset: back the short way, not every turn they made)
        private static void ShowCounter(Visual v, int count)
        {
            count = Math.Max(0, count);
            if (count == v.CounterShown) return;
            v.CounterShown = count;
            if (v.Counters == null) return;   // not attached yet: they start at this count
            for (int i = 0; i < v.Counters.Length; i++)
            {
                var c = v.Counters[i];
                float to = CounterAngle(i, count);
                if (c == null || c.To == to) continue;
                if (to == 0) { c.Angle %= 360f; c.To %= 360f; }
                Turn(c, to, 0, COUNTER_TURN_SECONDS);
            }
        }
        #endregion

        /// <summary>The Reload animation of this designator is playing (ANIMATION_START_FRAME .. ANIMATION_END_FRAME).</summary>
        public static bool IsTuning(IMyAutomaticRifleGun rifle)
        {
            Visual v;
            return rifle != null && visuals.TryGetValue(rifle.EntityId, out v) && Now < v.TuneEnd;
        }

        /// <summary>Reload pressed: the knob turns one mode's worth, between the grab and release frames.</summary>
        public static void StartTune(IMyAutomaticRifleGun rifle, PointerMode newMode)
        {
            if (rifle == null) return;
            var v = Get(rifle);
            var k = v.Knob;
            if (k.TurnStart >= 0) k.Angle = k.To;   // pressed again mid-turn: from where that turn ends
            else if (!v.PartsTried) k.Angle = (ScoutPointer.ModeIndex(newMode) - 1) * TUNER_DEGREES_PER_MODE;   // not attached yet
            Turn(k, k.Angle + TUNER_DEGREES_PER_MODE, (TUNER_GRAB_FRAME - ANIMATION_START_FRAME) / ANIMATION_FPS,
                 Math.Max(1, TUNER_RELEASE_FRAME - TUNER_GRAB_FRAME) / ANIMATION_FPS);
            v.TuneEnd = Now + (ANIMATION_END_FRAME - ANIMATION_START_FRAME) / ANIMATION_FPS;
            ShowMode(v, newMode);
        }

        #region Lights
        // The mode lamps: the current mode's lit in its color, the others off (tag mode: all three teal)
        private static void ShowMode(Visual v, PointerMode mode)
        {
            if (v.ModeShown == (int)mode) return;
            Color lit = ModeColor(mode);
            bool ok = true;
            for (int i = 0; i < MODE_LIGHTS.Length; i++)
                ok &= i == (int)mode || mode == PointerMode.Tag ? Emissive(v.Rifle, MODE_LIGHTS[i], lit, LIGHT_EMISSIVITY)
                                                                : Emissive(v.Rifle, MODE_LIGHTS[i], OFF_COLOR, OFF_EMISSIVITY);
            if (ok) v.ModeShown = (int)mode;
        }

        private static Color ModeColor(PointerMode mode)
        {
            return mode == PointerMode.Tag ? TAG_COLOR : mode == PointerMode.Red ? new Color(255, 40, 30)
                 : mode == PointerMode.Blue ? new Color(40, 120, 255) : new Color(40, 255, 60);
        }

        private static void ShowTagged(Visual v, bool on)
        {
            int want = on ? 1 : 0;
            if (v.TagShown == want) return;
            bool ok = on ? Emissive(v.Rifle, TAG_LIGHT, TAG_COLOR, LIGHT_EMISSIVITY) : Emissive(v.Rifle, TAG_LIGHT, OFF_COLOR, OFF_EMISSIVITY);
            if (ok) v.TagShown = want;
        }

        /// <summary>Charge progress 0-1 lights CHARGE_LIGHTS in order (green, the last one in the mode's color);
        /// below 0 turns them all off.</summary>
        public static void SetCharge(IMyAutomaticRifleGun rifle, float progress, PointerMode mode)
        {
            if (rifle == null) return;
            var v = Get(rifle);
            int step = progress < 0 ? 0 : Math.Min(CHARGE_GLOW_STEPS, (int)Math.Ceiling(progress * CHARGE_GLOW_STEPS));
            if (step != v.GlowStep)
            {
                if (step > 0) Emissive(rifle, CHARGE_GLOW, CHARGE_COLOR, LIGHT_EMISSIVITY * step / CHARGE_GLOW_STEPS);
                else Emissive(rifle, CHARGE_GLOW, OFF_COLOR, OFF_EMISSIVITY);
                v.GlowStep = step;
            }
            int n = CHARGE_LIGHTS.Length;
            int lit = progress < 0 ? 0 : Math.Min(n, (int)Math.Ceiling(progress * n));
            if (lit == v.LitCharge) return;
            Color last = (ScoutPointer.KindOf(rifle) == PointerKind.Combat ? CHARGE_LAST_HOSTILE : CHARGE_LAST_INDUSTRIAL)[(int)mode];
            for (int i = 0; i < n; i++)
            {
                if (i < lit) Emissive(rifle, CHARGE_LIGHTS[i], i == n - 1 ? last : CHARGE_COLOR, LIGHT_EMISSIVITY);
                else Emissive(rifle, CHARGE_LIGHTS[i], OFF_COLOR, OFF_EMISSIVITY);
            }
            v.LitCharge = lit;
        }

        private static bool Emissive(IMyAutomaticRifleGun rifle, string material, Color color, float emissivity)
        {
            var ent = rifle as MyEntity;
            if (ent == null || !ent.InScene) return false;
            try
            {
                ent.SetEmissiveParts(material, color, emissivity);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
        #endregion

        #region Sounds
        public static void PlayChargeSound(IMyAutomaticRifleGun rifle)
        {
            var ent = rifle as MyEntity;
            if (ent == null) return;
            var v = Get(rifle);
            if (chargeSound == null) chargeSound = new MySoundPair(CHARGE_SOUND);
            if (v.Emitter == null) v.Emitter = new MyEntity3DSoundEmitter(ent);
            // The holder's own feedback: played 2D (the 3D wave from the hand item was only heard in first person),
            // and heard in realistic sound mode too
            v.Emitter.PlaySound(chargeSound, stopPrevious: true, alwaysHearOnRealistic: true, force2D: true);
        }

        public static void StopChargeSound(IMyAutomaticRifleGun rifle)
        {
            Visual v;
            if (rifle != null && visuals.TryGetValue(rifle.EntityId, out v) && v.Emitter != null) v.Emitter.StopSound(true);
        }

        /// <summary>A designator signal's sound for this player, 2D: error, request (one chirp) or ok (three).</summary>
        public static void PlayFeedback(DesignatorSignal signal)
        {
            if (MyAPIGateway.Utilities.IsDedicated || MyAPIGateway.Session == null || MyAPIGateway.Session.Camera == null) return;
            if (okSound == null) { okSound = new MySoundPair(OK_SOUND); errorSound = new MySoundPair(ERROR_SOUND); requestSound = new MySoundPair(REQUEST_SOUND); }
            if (feedbackEmitter == null) feedbackEmitter = new MyEntity3DSoundEmitter(null);
            feedbackEmitter.SetPosition(MyAPIGateway.Session.Camera.WorldMatrix.Translation);
            var sound = signal == DesignatorSignal.Error ? errorSound : signal == DesignatorSignal.Requested ? requestSound : okSound;
            feedbackEmitter.PlaySound(sound, stopPrevious: true, alwaysHearOnRealistic: true, force2D: true);
        }
        #endregion

        #region Signal blinks (the 5 status lights; DesignatorSignal)
        /// <summary>Three blinks, on while each of the ok sound's three beeps sounds (AutomataDesignatorOk.wav, measured:
        /// seconds from its start).</summary>
        public static readonly double[] BLINK_OK = { 0.00, 0.07, 0.10, 0.21, 0.23, 0.34 };
        /// <summary>Error: one blink, the error sound's length (AutomataDesignatorError.wav: 0.3 s).</summary>
        public static readonly double[] BLINK_ERROR = { 0.00, 0.30 };
        public static readonly Color BLINK_OK_COLOR = new Color(40, 255, 60);         // tag ok, spot check / engage confirmed
        public static readonly Color BLINK_REQUESTED_COLOR = new Color(255, 210, 0);  // yellow: order sent
        public static readonly Color BLINK_BLUE_COLOR = new Color(40, 120, 255);      // area sweep / escort confirmed
        public static readonly Color BLINK_RECALL_COLOR = new Color(10, 120, 25);     // darker green
        public static readonly Color BLINK_ERROR_COLOR = new Color(255, 30, 20);

        public static void StartBlink(IMyAutomaticRifleGun rifle, DesignatorSignal signal)
        {
            if (rifle == null || rifle.MarkedForClose || rifle.Closed) return;
            var v = Get(rifle);
            v.BlinkStart = Now;
            v.BlinkWindows = signal == DesignatorSignal.Error ? BLINK_ERROR : BLINK_OK;
            v.BlinkColor = signal == DesignatorSignal.Error ? BLINK_ERROR_COLOR
                         : signal == DesignatorSignal.Requested ? BLINK_REQUESTED_COLOR
                         : signal == DesignatorSignal.ConfirmedBlue ? BLINK_BLUE_COLOR
                         : signal == DesignatorSignal.Recall ? BLINK_RECALL_COLOR : BLINK_OK_COLOR;
            v.BlinkLit = -1;
        }

        public static void StopBlink(IMyAutomaticRifleGun rifle)
        {
            Visual v;
            if (rifle == null || !visuals.TryGetValue(rifle.EntityId, out v) || v.BlinkStart < 0) return;
            v.BlinkStart = -1;
            BlinkLights(v, false);
        }

        private static void UpdateBlink(Visual v)
        {
            if (v.BlinkStart < 0) return;
            double t = Now - v.BlinkStart;
            var windows = v.BlinkWindows;
            if (t >= windows[windows.Length - 1]) { v.BlinkStart = -1; BlinkLights(v, false); return; }
            bool on = false;
            for (int i = 0; i + 1 < windows.Length; i += 2) if (t >= windows[i] && t < windows[i + 1]) { on = true; break; }
            int lit = on ? 1 : 0;
            if (lit == v.BlinkLit) return;
            v.BlinkLit = lit;
            BlinkLights(v, on);
        }

        // All the charge lights in the blink color, or off (the next charge redraws them: LitCharge reset)
        private static void BlinkLights(Visual v, bool on)
        {
            for (int i = 0; i < CHARGE_LIGHTS.Length; i++)
                if (on) Emissive(v.Rifle, CHARGE_LIGHTS[i], v.BlinkColor, LIGHT_EMISSIVITY);
                else Emissive(v.Rifle, CHARGE_LIGHTS[i], OFF_COLOR, OFF_EMISSIVITY);
            if (!on) v.BlinkLit = 0;
            v.LitCharge = -1;
        }
        #endregion
    }
}
