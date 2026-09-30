using System;
using System.Collections.Generic;

using Sandbox.Definitions;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRage.ModAPI;
using VRageMath;

using Automata.Inventory;
using Automata.Util;
using Automata.Util.Logging;

namespace Automata.Construction
{
    /// <summary>
    /// Area scanning, ordering and batching. Used by stand-alone drones (observation area) and meant to be reused by
    /// the Orchestrator / ConstructionComputerBlock at a larger scale.
    ///
    /// Ordering is central-out: blocks nearest the area centre are built first, so the outer shell - which would wall
    /// the drone out of the inside - comes last, and the result grows symmetrically.
    /// </summary>
    public partial class ConstructionComputer
    {
        private struct Candidate
        {
            public IMySlimBlock Block;           // real block, or projected block
            public IMyProjector Projector;       // projections only
            public Orchestrator.JobType Type;
            public Vector3D Center;              // world
            public double SortKey;               // squared distance to the area centre, area frame
        }

        private readonly List<IMyCubeGrid> _areaGrids = new List<IMyCubeGrid>();
        private readonly List<IMySlimBlock> _areaBlocks = new List<IMySlimBlock>();
        private readonly List<IMySlimBlock> _projectedBlocks = new List<IMySlimBlock>();
        private readonly List<Candidate> _candidates = new List<Candidate>();
        private readonly Dictionary<string, int> _missingCache = new Dictionary<string, int>();
        private readonly HashSet<IMySlimBlock> _cellBlocks = new HashSet<IMySlimBlock>();

        /// <summary>m: how far out from a block its approach point lies (the straight-in part of the approach).</summary>
        public const double APPROACH_DISTANCE = 5.0;

        // Blocks that only touch the area with a face are not in it
        private const double AREA_EPSILON = 0.05;
        // Walk the area's cells instead of every block of the grid when the area covers at most this many cells
        private const int MAX_CELL_WALK = 4096;

        /// <summary>
        /// Finds blocks to weld / repair / build (projections) / grind that intersect the area (an oriented box), on
        /// the grids logically linked to 'rootGrid' (connectors, rotors, pistons, hinges; merged grids are one grid;
        /// landing gear does not link). Grids in 'exclude' (the drone itself) are skipped. The nearest-to-centre
        /// 'maxJobs' targets are appended to 'result', central-out. <see cref="PlanJob"/> turns them into trips.
        /// </summary>
        public int ScanArea(IMyCubeGrid rootGrid, ref MatrixD areaMatrix, ref Vector3D areaHalfExtents,
                            WorkModes modes, Vector3 grindColorMask, List<IMyCubeGrid> exclude, int maxJobs,
                            List<ConstructionTarget> result)
        {
            if (rootGrid == null || maxJobs <= 0) return 0;
            bool weldBuilt = (modes & (WorkModes.WeldUnfinishedBlocks | WorkModes.RepairDamagedBlocks)) != 0;
            bool weldProjected = (modes & WorkModes.WeldProjectedBlocks) != 0;
            bool grind = (modes & WorkModes.Grind) != 0;
            if (!weldBuilt && !weldProjected && !grind)
                return 0;

            Vector3D half = Vector3D.Max(areaHalfExtents - AREA_EPSILON, new Vector3D(AREA_EPSILON));
            var areaObb = new MyOrientedBoundingBoxD(areaMatrix.Translation, half, Quaternion.CreateFromRotationMatrix(areaMatrix));
            Vector3D areaCenter = areaMatrix.Translation;
            double areaRadius = half.Length();
            MatrixD areaInv = MatrixD.Transpose(areaMatrix);   // rotation only; used with TransformNormal
            // Gravity where the work is: no faces from below (floors)
            float interference;
            Vector3D gravity = MyAPIGateway.Physics.CalculateNaturalGravityAt(areaCenter, out interference);
            Vector3D gravityUp = gravity.LengthSquared() > 1e-4 ? -Vector3D.Normalize(gravity) : Vector3D.Zero;
            Vector3I cell, min, max;

            _candidates.Clear();
            _areaGrids.Clear();
            rootGrid.GetGridGroup(GridLinkTypeEnum.Logical).GetGrids(_areaGrids);

            for (int g = 0; g < _areaGrids.Count; g++)
            {
                var grid = _areaGrids[g];
                if (grid.Physics == null || grid.MarkedForClose || (exclude != null && exclude.Contains(grid))) continue;

                // Built blocks: only grids that reach the area
                if ((weldBuilt || grind) && grid.WorldAABB.Intersects(new BoundingSphereD(areaCenter, areaRadius)))
                {
                    _areaBlocks.Clear();
                    CollectBlocks(grid, ref areaMatrix, ref half, _areaBlocks);
                    for (int i = 0; i < _areaBlocks.Count; i++)
                    {
                        var b = _areaBlocks[i];
                        Orchestrator.JobType type;
                        if (grind && ColorUtil.ColorMatch(b, grindColorMask)) type = Orchestrator.JobType.Grind;
                        else if (weldBuilt && NeedsWeld(b, modes)) type = Orchestrator.JobType.Weld;
                        else continue;
                        Vector3D center;
                        if (!IntersectsArea(b, ref areaObb, areaCenter, areaRadius, out center)) continue;
                        double key = SortKeyOf(center, ref areaCenter, ref areaInv);
                        if (!WouldKeep(key, maxJobs)) continue;
                        // Walled in (inside a solid chunk): unreachable, and must not take a slot
                        if (!HasFreeFace(grid, b.Min, b.Max, ref gravityUp)) continue;
                        AddCandidate(b, null, type, center, key, maxJobs);
                    }
                    _areaBlocks.Clear();
                }

                // Projections: the projector can be anywhere on these grids
                if (weldProjected)
                {
                    foreach (var projector in grid.GetFatBlocks<IMyProjector>())
                    {
                        if (!projector.IsWorking || !projector.IsProjecting) continue;
                        var projected = projector.ProjectedGrid;
                        if (projected == null || !projected.WorldAABB.Intersects(new BoundingSphereD(areaCenter, areaRadius))) continue;
                        _projectedBlocks.Clear();
                        CollectBlocks(projected, ref areaMatrix, ref half, _projectedBlocks);
                        for (int i = 0; i < _projectedBlocks.Count; i++)
                        {
                            var b = _projectedBlocks[i];
                            Vector3D center;
                            if (!IntersectsArea(b, ref areaObb, areaCenter, areaRadius, out center)) continue;
                            double key = SortKeyOf(center, ref areaCenter, ref areaInv);
                            if (!WouldKeep(key, maxJobs)) continue;   // before CanBuild, which is the expensive part
                            PhysicalExtent(b, grid, out cell, out min, out max);
                            if (!HasFreeFace(grid, min, max, ref gravityUp)) continue;
                            // Buildable right now: attached to built blocks and not obstructed
                            if (projector.CanBuild(b, true) != BuildCheckResult.OK) continue;
                            AddCandidate(b, projector, Orchestrator.JobType.Weld, center, key, maxJobs);
                        }
                        _projectedBlocks.Clear();
                    }
                }
            }
            _areaGrids.Clear();

            int count = 0;   // candidates are already sorted and capped
            for (int i = 0; i < _candidates.Count; i++)
            {
                Candidate c = _candidates[i];
                ConstructionTarget target;
                if (!CreateTarget(ref c, ref areaCenter, ref gravityUp, out target)) continue;   // no free face: unreachable
                result.Add(target);
                count++;
            }
            _candidates.Clear();
            return count;
        }

        private static bool NeedsWeld(IMySlimBlock b, WorkModes modes)
        {
            if ((modes & WorkModes.WeldUnfinishedBlocks) != 0 && b.BuildLevelRatio < 1.0f) return true;
            if ((modes & WorkModes.RepairDamagedBlocks) != 0 && (b.CurrentDamage > 0.0f || b.HasDeformation)) return true;
            return false;
        }

        // Block box (its cells, in its grid's frame) against the area box. Cheap sphere test first.
        private static bool IntersectsArea(IMySlimBlock b, ref MyOrientedBoundingBoxD area, Vector3D areaCenter, double areaRadius,
                                           out Vector3D center)
        {
            var grid = b.CubeGrid;
            double size = grid.GridSize;
            Vector3D localCenter = (Vector3D)(b.Min + b.Max) * (size * 0.5);
            MatrixD gm = grid.WorldMatrix;
            center = Vector3D.Transform(localCenter, gm);
            Vector3D half = (Vector3D)(b.Max - b.Min + Vector3I.One) * (size * 0.5);
            double r = areaRadius + half.Length();
            if (Vector3D.DistanceSquared(center, areaCenter) > r * r) return false;
            var blockObb = new MyOrientedBoundingBoxD(center, half, Quaternion.CreateFromRotationMatrix(gm));
            return area.Intersects(ref blockObb);
        }

        private static double SortKeyOf(Vector3D center, ref Vector3D areaCenter, ref MatrixD areaInv)
        {
            return Vector3D.TransformNormal(center - areaCenter, areaInv).LengthSquared();
        }

        // Candidates are kept sorted and capped at maxJobs (bounded insertion; System.Comparison is not whitelisted)
        private bool WouldKeep(double key, int maxJobs)
        {
            return _candidates.Count < maxJobs || key < _candidates[_candidates.Count - 1].SortKey;
        }

        private void AddCandidate(IMySlimBlock b, IMyProjector projector, Orchestrator.JobType type, Vector3D center,
                                  double key, int maxJobs)
        {
            var c = new Candidate { Block = b, Projector = projector, Type = type, Center = center, SortKey = key };
            if (_candidates.Count >= maxJobs) _candidates.RemoveAt(_candidates.Count - 1);
            int i = _candidates.Count;
            while (i > 0 && _candidates[i - 1].SortKey > key) i--;
            _candidates.Insert(i, c);
        }

        /// <summary>
        /// Blocks of 'grid' that may touch the area. Small areas on big grids walk the area's cells (each block once);
        /// otherwise every block of the grid. Callers still run the exact intersection test.
        /// </summary>
        private void CollectBlocks(IMyCubeGrid grid, ref MatrixD areaMatrix, ref Vector3D half, List<IMySlimBlock> output)
        {
            Vector3I min = Vector3I.MaxValue, max = Vector3I.MinValue;
            Vector3D c = areaMatrix.Translation;
            Vector3D ax = areaMatrix.Right * half.X, ay = areaMatrix.Up * half.Y, az = areaMatrix.Backward * half.Z;
            for (int k = 0; k < 8; k++)
            {
                Vector3D p = c + ((k & 1) != 0 ? ax : -ax) + ((k & 2) != 0 ? ay : -ay) + ((k & 4) != 0 ? az : -az);
                Vector3I cell = grid.WorldToGridInteger(p);
                min = Vector3I.Min(min, cell);
                max = Vector3I.Max(max, cell);
            }
            // Multi-cell blocks can start outside the area: pad by one cell, then clamp to the grid
            min = Vector3I.Max(min - Vector3I.One, grid.Min);
            max = Vector3I.Min(max + Vector3I.One, grid.Max);
            if (min.X > max.X || min.Y > max.Y || min.Z > max.Z) return;
            long cells = (long)(max.X - min.X + 1) * (max.Y - min.Y + 1) * (max.Z - min.Z + 1);
            if (cells > MAX_CELL_WALK)
            {
                grid.GetBlocks(output);
                return;
            }
            _cellBlocks.Clear();
            Vector3I pos;
            for (pos.X = min.X; pos.X <= max.X; pos.X++)
                for (pos.Y = min.Y; pos.Y <= max.Y; pos.Y++)
                    for (pos.Z = min.Z; pos.Z <= max.Z; pos.Z++)
                    {
                        var b = grid.GetCubeBlock(pos);
                        if (b != null && _cellBlocks.Add(b)) output.Add(b);
                    }
            _cellBlocks.Clear();
        }

        private bool CreateTarget(ref Candidate c, ref Vector3D areaCenter, ref Vector3D gravityUp, out ConstructionTarget target)
        {
            target = default(ConstructionTarget);
            var b = c.Block;
            // Projections are tracked in the projector's grid: that's where the block will exist once built
            IMyCubeGrid physical = c.Projector != null ? c.Projector.CubeGrid : b.CubeGrid;
            Vector3I cell, min, max;
            PhysicalExtent(b, physical, out cell, out min, out max);

            Vector3I faceCell, normal;
            if (!ChooseFace(physical, min, max, areaCenter, gravityUp, 1, null, out faceCell, out normal)) return false;

            _missingCache.Clear();
            if (c.Type == Orchestrator.JobType.Weld)
            {
                if (c.Projector != null) AddDefinitionComponents(b, _missingCache);
                else b.GetMissingComponents(_missingCache);
            }
            else AddMountedComponentsForGrind(b, _missingCache);

            target = new ConstructionTarget
            {
                Components = new DiscreteInventory(_missingCache),
                Center = c.Center,
                GridEntityId = physical.EntityId,
                Cell = cell,
                FaceCell = faceCell,
                FaceNormal = normal,
                Type = c.Type,
                IsProjected = c.Projector != null,
            };
            return true;
        }

        // Every component of the block (projections have none mounted yet)
        private static void AddDefinitionComponents(IMySlimBlock b, Dictionary<string, int> into)
        {
            var def = b.BlockDefinition as MyCubeBlockDefinition;
            if (def == null || def.Components == null) return;
            for (int i = 0; i < def.Components.Length; i++)
            {
                var comp = def.Components[i];
                string name = comp.Definition.Id.SubtypeName;
                int cur;
                into.TryGetValue(name, out cur);
                into[name] = cur + comp.Count;
            }
        }

        private static readonly Random _faceRandom = new Random();

        /// <summary>
        /// A block's position cell and extent in 'physical' (its own grid, or for a projected block the projector's
        /// grid it will be built in: projections are offset by whole cells and turned in 90° steps).
        /// </summary>
        public static void PhysicalExtent(IMySlimBlock b, IMyCubeGrid physical, out Vector3I cell, out Vector3I min, out Vector3I max)
        {
            var own = b.CubeGrid;
            if (own == physical)
            {
                cell = b.Position;
                min = b.Min;
                max = b.Max;
                return;
            }
            cell = physical.WorldToGridInteger(own.GridIntegerToWorld(b.Position));
            Vector3I a = physical.WorldToGridInteger(own.GridIntegerToWorld(b.Min));
            Vector3I z = physical.WorldToGridInteger(own.GridIntegerToWorld(b.Max));
            min = Vector3I.Min(a, z);
            max = Vector3I.Max(a, z);
        }

        /// <summary>
        /// Extent of the block that is, or will be (a projection of one of the grid's projectors), at 'cell' of 'grid'.
        /// False when there is neither.
        /// </summary>
        public static bool TryGetBlockExtent(IMyCubeGrid grid, Vector3I cell, out Vector3I min, out Vector3I max)
        {
            Vector3I position;
            var slim = grid.GetCubeBlock(cell);
            if (slim != null)
            {
                PhysicalExtent(slim, grid, out position, out min, out max);
                return true;
            }
            Vector3D world = grid.GridIntegerToWorld(cell);
            foreach (var projector in grid.GetFatBlocks<IMyProjector>())
            {
                var projected = projector.IsProjecting ? projector.ProjectedGrid : null;
                if (projected == null) continue;
                slim = projected.GetCubeBlock(projected.WorldToGridInteger(world));
                if (slim == null) continue;
                PhysicalExtent(slim, grid, out position, out min, out max);
                return true;
            }
            min = max = cell;
            return false;
        }

        /// <summary>Cheap pre-check for <see cref="ChooseFace"/>: does the block have any face it could be reached from?</summary>
        private static bool HasFreeFace(IMyCubeGrid grid, Vector3I min, Vector3I max, ref Vector3D gravityUp)
        {
            MatrixD m = grid.WorldMatrix;
            bool inGravity = gravityUp.LengthSquared() > 0.5;
            for (int i = 0; i < 6; i++)
            {
                Vector3I d = Base6Directions.GetIntVector((Base6Directions.Direction)i);
                if (inGravity && Vector3D.Dot(Vector3D.TransformNormal((Vector3D)d, m), gravityUp) < -0.7) continue;
                Vector3I lo = min, hi = max;
                if (d.X > 0) lo.X = max.X; else if (d.X < 0) hi.X = min.X;
                if (d.Y > 0) lo.Y = max.Y; else if (d.Y < 0) hi.Y = min.Y;
                if (d.Z > 0) lo.Z = max.Z; else if (d.Z < 0) hi.Z = min.Z;
                Vector3I c;
                for (c.X = lo.X; c.X <= hi.X; c.X++)
                    for (c.Y = lo.Y; c.Y <= hi.Y; c.Y++)
                        for (c.Z = lo.Z; c.Z <= hi.Z; c.Z++)
                            if (!grid.CubeExists(c + d)) return true;
            }
            return false;
        }

        /// <summary>
        /// Picks where a tool works on a block: the centre of one face of one of its cells (never an edge or a
        /// vertex, which would reach into the neighbouring blocks), approached along that face's normal.
        /// Only faces on the outside of the block (min..max, cells of 'grid') whose neighbouring cell is free count
        /// (you can't weld through a wall). Among those:
        /// - never from below in gravity (floors), slightly preferring above,
        /// - preferably outwards from the area centre (build inside first, keep the drone outside),
        /// - the line out along the normal ('clearCells' deep) and the cells around it free: built blocks there
        ///   count against the face (the drone would sit in them, or scrape them / the floor),
        /// - optional 'extraCost' per world normal (the drone attitude that side needs, which side it is on...),
        /// - ties broken at random.
        /// False when no face is free: the block can't be reached right now.
        /// </summary>
        /// <param name="normal">Unit axis of 'grid', pointing out of the face towards the tool.</param>
        public static bool ChooseFace(IMyCubeGrid grid, Vector3I min, Vector3I max, Vector3D areaCenter, Vector3D gravityUp,
                                      int clearCells, Func<Vector3D, double> extraCost, out Vector3I faceCell, out Vector3I normal)
        {
            faceCell = min;
            normal = Vector3I.Zero;
            MatrixD m = grid.WorldMatrix;
            bool inGravity = gravityUp.LengthSquared() > 0.5;
            Vector3D blockCenter = Vector3D.Transform((Vector3D)(min + max) * (grid.GridSize * 0.5), m);
            Vector3D outward = blockCenter - areaCenter;
            if (outward.LengthSquared() < 1e-4) outward = inGravity ? gravityUp : m.Up;
            outward.Normalize();
            if (clearCells < 1) clearCells = 1;

            // Grid axis closest to "down"
            Vector3I down = Vector3I.Zero;
            if (inGravity)
            {
                double bestDown = 0.5;
                for (int i = 0; i < 6; i++)
                {
                    Vector3I d = Base6Directions.GetIntVector((Base6Directions.Direction)i);
                    double dot = -Vector3D.Dot(Vector3D.TransformNormal((Vector3D)d, m), gravityUp);
                    if (dot > bestDown) { bestDown = dot; down = d; }
                }
            }

            double bestScore = double.MinValue;
            for (int i = 0; i < 6; i++)
            {
                Vector3I d = Base6Directions.GetIntVector((Base6Directions.Direction)i);
                Vector3D w = Vector3D.TransformNormal((Vector3D)d, m);
                double sideScore = Vector3D.Dot(w, outward);                    // outwards first
                if (inGravity)
                {
                    double up = Vector3D.Dot(w, gravityUp);
                    if (up < -0.7) continue;                                    // from below: through the floor
                    sideScore += up * 0.25;                                     // slight preference for above
                }
                if (extraCost != null) sideScore -= extraCost(w);
                if (sideScore + 0.1 <= bestScore) continue;                     // no face on this side can win

                // The block's outer layer of cells on this side, and the two axes across it
                Vector3I e1 = Base6Directions.GetIntVector(Base6Directions.GetPerpendicular((Base6Directions.Direction)i));
                Vector3I e2;
                Vector3I.Cross(ref d, ref e1, out e2);
                Vector3I lo = min, hi = max;
                if (d.X > 0) lo.X = max.X; else if (d.X < 0) hi.X = min.X;
                if (d.Y > 0) lo.Y = max.Y; else if (d.Y < 0) hi.Y = min.Y;
                if (d.Z > 0) lo.Z = max.Z; else if (d.Z < 0) hi.Z = min.Z;
                Vector3I c;
                for (c.X = lo.X; c.X <= hi.X; c.X++)
                    for (c.Y = lo.Y; c.Y <= hi.Y; c.Y++)
                        for (c.Z = lo.Z; c.Z <= hi.Z; c.Z++)
                        {
                            if (grid.CubeExists(c + d)) continue;               // covered: not a face
                            double score = sideScore;
                            for (int k = 1; k <= clearCells; k++)
                            {
                                Vector3I p = c + d * k;
                                if (k > 1 && grid.CubeExists(p)) score -= 3;    // the hull would sit in a block
                                // Tight spots: blocks around the line (the floor worst: the hull hangs below the tool)
                                if (grid.CubeExists(p + e1)) score -= e1 == down ? 1 : 0.25;
                                if (grid.CubeExists(p - e1)) score -= -e1 == down ? 1 : 0.25;
                                if (grid.CubeExists(p + e2)) score -= e2 == down ? 1 : 0.25;
                                if (grid.CubeExists(p - e2)) score -= -e2 == down ? 1 : 0.25;
                            }
                            score += _faceRandom.NextDouble() * 0.1;            // tie-break
                            if (score > bestScore) { bestScore = score; faceCell = c; normal = d; }
                        }
            }
            return normal != Vector3I.Zero;
        }

        /// <summary>World centre of the face of 'cell' on side 'normal'; 'worldNormal' is that side as a world unit vector.</summary>
        public static Vector3D FacePoint(IMyCubeGrid grid, Vector3I cell, Vector3I normal, out Vector3D worldNormal)
        {
            worldNormal = Vector3D.TransformNormal((Vector3D)normal, grid.WorldMatrix);
            return grid.GridIntegerToWorld(cell) + worldNormal * (grid.GridSize * 0.5);
        }

        /// <summary>
        /// The cell face a planned tool position is on: 'point' (a face centre) and 'worldNormal' (its normal) back to
        /// grid cell + axis. False when the normal is not along a grid axis (the grid has turned a lot since).
        /// </summary>
        public static bool FaceOf(IMyCubeGrid grid, Vector3D point, Vector3D worldNormal, out Vector3I cell, out Vector3I normal)
        {
            normal = Vector3I.Zero;
            MatrixD m = grid.WorldMatrix;
            double best = 0.9;
            for (int i = 0; i < 6; i++)
            {
                Vector3I d = Base6Directions.GetIntVector((Base6Directions.Direction)i);
                double dot = Vector3D.Dot(Vector3D.TransformNormal((Vector3D)d, m), worldNormal);
                if (dot > best) { best = dot; normal = d; }
            }
            cell = Vector3I.Zero;
            if (normal == Vector3I.Zero) return false;
            cell = grid.WorldToGridInteger(point - Vector3D.TransformNormal((Vector3D)normal, m) * (grid.GridSize * 0.5));
            return true;
        }

        /// <summary>
        /// Plans ONE trip from 'ordered' targets (central-out): takes targets in order while their components are
        /// available and the load stays within 'maxMass' (kg) and 'maxVolume' (m³); targets whose components the
        /// network lacks are skipped (reported in 'missing'). One job type per trip (the first target's). Grind
        /// targets need room and lift for what they return.
        /// Tasks: [load] + (NavigateToolTo a cell face, Weld/Grind) per block + ReturnHome + Unload(everything). Positions are
        /// world, or in 'beacon's frame (Right, Up, Forward) when given, so work on a moving base follows it.
        /// Returns null when nothing fits.
        /// </summary>
        public Orchestrator.Job PlanJob(List<ConstructionTarget> ordered, DiscreteInventory available, double maxMass, double maxVolume,
                                        IMyEntity beacon, float naturalGravity, DiscreteInventory missing)
        {
            if (ordered.Count == 0) return null;
            var lookup = AutomataSession.componentLookup;
            MatrixD am = beacon != null ? beacon.WorldMatrix : MatrixD.Identity;
            double mass = 0, volume = 0;
            var remaining = new DiscreteInventory(available);
            var load = new DiscreteInventory();
            var items = new List<KVPair>();
            Orchestrator.Job job = null;
            for (int i = 0; i < ordered.Count; i++)
            {
                var target = ordered[i];
                if (job != null && target.Type != job.JobType) continue;
                if (!MyAPIGateway.Entities.EntityExists(target.GridEntityId)) continue;   // grid gone since the scan
                double jobMass = 0, jobVolume = 0;
                bool ok = true;
                items.Clear();
                if (target.Components != null) target.Components.GetAllItems(items);
                for (int k = 0; k < items.Count; k++)
                {
                    ComponentData data;
                    if (lookup.TryGetValue(items[k].Key, out data))
                    {
                        jobMass += data.Mass * items[k].Value;
                        jobVolume += data.Volume * items[k].Value;
                    }
                    if (target.Type == Orchestrator.JobType.Weld && remaining.GetItemCount(items[k].Key) < items[k].Value)
                    {
                        ok = false;
                        missing.AddItem(items[k].Key, items[k].Value - remaining.GetItemCount(items[k].Key));
                    }
                }
                if (!ok) continue;
                // Too big for what is left; smaller ones may still fit. Grinding: what it returns must fit and lift.
                if (mass + jobMass > maxMass || volume + jobVolume > maxVolume) continue;
                mass += jobMass;
                volume += jobVolume;
                if (target.Type == Orchestrator.JobType.Weld)
                {
                    for (int k = 0; k < items.Count; k++)
                    {
                        remaining.RemoveItem(items[k].Key, items[k].Value);
                        load.AddItem(items[k].Key, items[k].Value);
                    }
                }
                if (job == null) job = NewJob(target.Type);
                AddWorkPair(job, ref target, beacon, ref am, naturalGravity, false);
            }
            if (job == null) job = PlanPartialJob(ordered, remaining, maxMass, maxVolume, beacon, ref am, naturalGravity, load);
            if (job == null) return null;
            if (job.JobType == Orchestrator.JobType.Weld) job.Tasks.Insert(0, Orchestrator.Task.Load(load));
            job.Tasks.Add(Orchestrator.Task.ReturnHome());
            job.Tasks.Add(Orchestrator.Task.Unload());
            job.CalculateTotalInventory();
            return job;
        }

        private Orchestrator.Job NewJob(Orchestrator.JobType type)
        {
            return new Orchestrator.Job
            {
                JobId = IdGenerator.GenerateId(ref _jobIdCounter, entityId),
                JobType = type,
                CreatedTime = DateTime.UtcNow,
            };
        }

        // NavigateToolTo + the tool task for one block. 'partial': the drone carries only part of what the block
        // needs (weld: until its cargo has nothing more for the block) or can't take all it returns (grind: until full).
        private static void AddWorkPair(Orchestrator.Job job, ref ConstructionTarget target, IMyEntity beacon, ref MatrixD am,
                                        float naturalGravity, bool partial)
        {
            IMyEntity e;
            var grid = MyAPIGateway.Entities.TryGetEntityById(target.GridEntityId, out e) ? e as IMyCubeGrid : null;
            if (grid == null) return;
            // The tool goes on the chosen cell face, in along its normal
            Vector3D n;
            Vector3D point = FacePoint(grid, target.FaceCell, target.FaceNormal, out n);
            Vector3D approach = point + n * APPROACH_DISTANCE;
            job.Tasks.Add(beacon != null
                ? Orchestrator.Task.NavigateToolTo(beacon.EntityId, ToFrame(ref am, point), ToFrame(ref am, approach), naturalGravity)
                : Orchestrator.Task.NavigateToolTo(0, point, approach, naturalGravity));
            var tool = target.Type == Orchestrator.JobType.Weld
                ? Orchestrator.Task.Weld(target.GridEntityId, target.Cell, target.IsProjected)
                : Orchestrator.Task.Grind(target.GridEntityId, target.Cell);
            if (partial)
                tool.Completion = target.Type == Orchestrator.JobType.Weld
                    ? Orchestrator.ToolTaskCompletionTrigger.DroneInventoryEmpty
                    : Orchestrator.ToolTaskCompletionTrigger.DroneInventoryFull;
            job.Tasks.Add(tool);
            job.BlockCount++;
        }

        /// <summary>
        /// Nothing fits whole: the first (most central) block that is too big for one trip gets a partial one.
        /// Weld: a share of every component it still needs, as much as the drone can lift and hold (and the network
        /// has), welded until the drone has nothing more for it; the next trip brings the rest. Grind: ground until
        /// the drone is full; the next trip carries on.
        /// </summary>
        private Orchestrator.Job PlanPartialJob(List<ConstructionTarget> ordered, DiscreteInventory available, double maxMass, double maxVolume,
                                                IMyEntity beacon, ref MatrixD am, float naturalGravity, DiscreteInventory load)
        {
            if (maxMass <= 0 || maxVolume <= 0) return null;
            var lookup = AutomataSession.componentLookup;
            var items = new List<KVPair>();
            for (int i = 0; i < ordered.Count; i++)
            {
                var target = ordered[i];
                if (!MyAPIGateway.Entities.EntityExists(target.GridEntityId)) continue;
                items.Clear();
                if (target.Components != null) target.Components.GetAllItems(items);
                double mass = 0, volume = 0;
                for (int k = 0; k < items.Count; k++)
                {
                    ComponentData data;
                    if (!lookup.TryGetValue(items[k].Key, out data)) continue;
                    mass += data.Mass * items[k].Value;
                    volume += data.Volume * items[k].Value;
                }
                if (mass <= maxMass && volume <= maxVolume) continue;   // fits whole: not this one's turn (parts missing)

                if (target.Type == Orchestrator.JobType.Grind)
                {
                    var grindJob = NewJob(target.Type);
                    AddWorkPair(grindJob, ref target, beacon, ref am, naturalGravity, true);
                    return grindJob;
                }

                // The same share of every component: the block progresses through its construction stages
                double share = Math.Min(1.0, Math.Min(maxMass / Math.Max(mass, 1e-6), maxVolume / Math.Max(volume, 1e-9)));
                load.Clear();
                for (int k = 0; k < items.Count; k++)
                {
                    int want = (int)Math.Floor(items[k].Value * share);
                    int take = Math.Min(want, available.GetItemCount(items[k].Key));
                    if (take > 0) load.AddItem(items[k].Key, take);
                }
                if (load.IsEmpty()) continue;
                var job = NewJob(target.Type);
                AddWorkPair(job, ref target, beacon, ref am, naturalGravity, true);
                return job;
            }
            return null;
        }

        /// <summary>World point -> (Right, Up, Forward) of a block's frame.</summary>
        public static Vector3D ToFrame(ref MatrixD m, Vector3D world)
        {
            Vector3D d = world - m.Translation;
            return new Vector3D(Vector3D.Dot(d, m.Right), Vector3D.Dot(d, m.Up), Vector3D.Dot(d, m.Forward));
        }

        /// <summary>(Right, Up, Forward) of a block's frame -> world point.</summary>
        public static Vector3D FromFrame(ref MatrixD m, Vector3D local)
        {
            return m.Translation + m.Right * local.X + m.Up * local.Y + m.Forward * local.Z;
        }
    }

    /// <summary>One block to work on, from a scan. Planning turns a run of these into a job (one trip).</summary>
    public struct ConstructionTarget
    {
        /// <summary>Weld: components still missing. Grind: what grinding it returns.</summary>
        public DiscreteInventory Components;
        /// <summary>Block centre, world, at scan time (ordering only; tools never aim at it).</summary>
        public Vector3D Center;
        /// <summary>Physical grid (projections: the projector's grid, where the block will be built).</summary>
        public long GridEntityId;
        /// <summary>The block's position cell in that grid (what the tool task tracks).</summary>
        public Vector3I Cell;
        /// <summary>Where the tool works: the face of this cell (one of the block's) on side FaceNormal.</summary>
        public Vector3I FaceCell;
        /// <summary>Unit axis of the grid, out of the face towards the tool: the approach runs along it.</summary>
        public Vector3I FaceNormal;
        public Orchestrator.JobType Type;
        public bool IsProjected;
    }
}
