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
    /// Ordering: <see cref="BuildPlanner"/> (vertical slice through the centre first, then outwards; 3x3x3-cell units
    /// on small grids; units that would wall others in held back). The scan keeps the PLAN_POOL x maxJobs blocks
    /// nearest the area centre for it, then the first maxJobs of its order.
    /// </summary>
    public partial class ConstructionComputer
    {
        private struct Candidate
        {
            public IMySlimBlock Block;           // real block, or projected block
            public IMyProjector Projector;       // projections only
            public Orchestrator.JobType Type;
            public Vector3D Center;              // world
            public double SortKey;               // weld priority rank, then squared distance to the area centre, area frame
            public WorkCategory Category;
            public IMyCubeGrid Physical;         // where it is / will be built (projections: the projector's grid)
        }

        private const int PLAN_POOL = 4;           // blocks kept for the planner, per job wanted...
        private const int PLAN_POOL_MAX = 500;     // ...at most
        private readonly BuildPlanner _planner = new BuildPlanner();
        private readonly List<BuildPlanner.Item> _planItems = new List<BuildPlanner.Item>();
        private readonly List<int> _planOrder = new List<int>();
        private readonly List<IMyCubeGrid> _planGrids = new List<IMyCubeGrid>();

        private readonly List<IMyCubeGrid> _areaGrids = new List<IMyCubeGrid>();
        private readonly List<IMySlimBlock> _areaBlocks = new List<IMySlimBlock>();
        private readonly List<IMySlimBlock> _projectedBlocks = new List<IMySlimBlock>();
        private readonly List<Candidate> _candidates = new List<Candidate>();
        private readonly Dictionary<string, int> _missingCache = new Dictionary<string, int>();
        private readonly HashSet<IMySlimBlock> _cellBlocks = new HashSet<IMySlimBlock>();

        /// <summary>
        /// m: planned approach point distance (the side hint in NavigateToolTo / BackOut). Drones replace it with
        /// their own length + 2.5 m when they fly the task.
        /// </summary>
        public const double APPROACH_DISTANCE = 5.0;

        // Blocks that only touch the area with a face are not in it
        private const double AREA_EPSILON = 0.05;
        // Walk the area's cells instead of every block of the grid when the area covers at most this many cells
        private const int MAX_CELL_WALK = 4096;

        /// <summary>
        /// Finds blocks to weld / repair / build (projections) / grind that intersect the area (an oriented box), on
        /// the grids physically linked to 'rootGrid' (rotors, pistons, hinges, connectors, landing gear: a ship built
        /// on a locked connector or an anchoring landing gear counts; merged grids are one grid). Grids in 'exclude'
        /// (the drone itself) and those 'refuseGrid' gives a reason for (ownership, working drones) are skipped. Up to 'maxJobs' targets are
        /// appended to 'result' in build order (<see cref="BuildPlanner"/>). <see cref="PlanJob"/> turns them into trips.
        /// </summary>
        private readonly List<IMyCubeGrid> _extraGroup = new List<IMyCubeGrid>();

        public int ScanArea(IMyCubeGrid rootGrid, ref MatrixD areaMatrix, ref Vector3D areaHalfExtents,
                            WorkModes modes, Color grindColor, Color weldIgnoreColor, List<IMyCubeGrid> exclude, int maxJobs,
                            List<ConstructionTarget> result, Func<IMyCubeGrid, string> refuseGrid = null,
                            WorkPriority priority = WorkPriority.RepairProjectedUnbuilt, List<IMyCubeGrid> extraRoots = null,
                            bool shares = false)
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
            for (int k = 0; k < _poolCount.Length; k++) _poolCount[k] = 0;
            _areaGrids.Clear();
            rootGrid.GetGridGroup(GridLinkTypeEnum.Physical).GetGrids(_areaGrids);
            // Other structures (a drone's GPS observation area away from its home): their physical groups too
            if (extraRoots != null)
                for (int r = 0; r < extraRoots.Count; r++)
                {
                    if (extraRoots[r] == null || _areaGrids.Contains(extraRoots[r])) continue;
                    _extraGroup.Clear();
                    extraRoots[r].GetGridGroup(GridLinkTypeEnum.Physical).GetGrids(_extraGroup);
                    for (int k = 0; k < _extraGroup.Count; k++) if (!_areaGrids.Contains(_extraGroup[k])) _areaGrids.Add(_extraGroup[k]);
                    _extraGroup.Clear();
                }
            int pool = Math.Min(maxJobs * PLAN_POOL, Math.Max(maxJobs, PLAN_POOL_MAX));
            // Shares (construction computer): each kind (repair / projected / unbuilt / grind) keeps its own part of the
            // pool, 3 : 2 : 1 by weld priority rank (grind as the first), so a big backlog of one kind doesn't crowd out
            // the others; the computer publishes jobs of every kind at once. Without (stand-alone drone): strict order.
            _shares = shares;
            if (shares) SharesOf(priority, pool, _poolCap);
            double scanStart = Util.PerfStats.Start();
            var gridText = new System.Text.StringBuilder();
            int gridsScanned = 0, refused = 0, projectionsSeen = 0, projectionsIdle = 0, unbuildable = 0, obstructed = 0, walledIn = 0;

            for (int g = 0; g < _areaGrids.Count; g++)
            {
                var grid = _areaGrids[g];
                if (grid.Physics == null || grid.MarkedForClose || (exclude != null && exclude.Contains(grid))) continue;
                string refusal = refuseGrid != null ? refuseGrid(grid) : null;
                if (refusal != null)
                {
                    refused++;
                    gridText.Append(" [").Append(grid.DisplayName).Append(": skipped, ").Append(refusal).Append(']');
                    continue;
                }
                gridsScanned++;
                gridText.Append(" [").Append(grid.DisplayName).Append(grid.GridSizeEnum == VRage.Game.MyCubeSize.Small ? " small" : " large")
                        .Append(grid.IsStatic ? " static" : "").Append(']');

                // Built blocks: only grids that reach the area
                if ((weldBuilt || grind) && grid.WorldAABB.Intersects(new BoundingSphereD(areaCenter, areaRadius)))
                {
                    _areaBlocks.Clear();
                    CollectBlocks(grid, ref areaMatrix, ref half, _areaBlocks);
                    for (int i = 0; i < _areaBlocks.Count; i++)
                    {
                        var b = _areaBlocks[i];
                        Orchestrator.JobType type;
                        // Exact colours (ColorUtil.SameColor): the grind color is ground; the weld-ignore color is never welded
                        if (grind && ColorUtil.SameColor(b, grindColor)) type = Orchestrator.JobType.Grind;
                        else if (weldBuilt && NeedsWeld(b, modes) && !ColorUtil.SameColor(b, weldIgnoreColor)) type = Orchestrator.JobType.Weld;
                        else continue;
                        var category = type == Orchestrator.JobType.Grind ? WorkCategory.Grind : CategoryOf(b, modes);
                        Vector3D center;
                        if (!IntersectsArea(b, ref areaObb, areaCenter, areaRadius, out center)) continue;
                        double key = SortKeyOf(center, ref areaCenter, ref areaInv, WorkPriorities.Rank(priority, category));
                        if (!WouldKeep(key, category, pool)) continue;
                        // Walled in (inside a solid chunk): unreachable, and must not take a slot
                        if (!HasFreeFace(grid, b.Min, b.Max, ref gravityUp)) { walledIn++; continue; }
                        AddCandidate(b, null, grid, type, center, key, pool, category);
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
                        // Only blocks buildable right now are looked at (no planning ahead). The projector counts them itself
                        // (CanBuild without the physics test, rechecked by the game at most every 2 s after a block is added /
                        // removed on its grid, at once on an offset change): none, the projection is ignored; else the scan
                        // stops once that many were found in the area. A count up to 2 s old: one scan finds fewer, the next
                        // catches up.
                        int buildable = projector.BuildableBlocksCount;
                        if (buildable <= 0) { projectionsIdle++; continue; }
                        projectionsSeen++;
                        _projectedBlocks.Clear();
                        CollectBlocks(projected, ref areaMatrix, ref half, _projectedBlocks);
                        int found = 0;
                        for (int i = 0; i < _projectedBlocks.Count && found < buildable; i++)
                        {
                            var b = _projectedBlocks[i];
                            if (ColorUtil.SameColor(b, weldIgnoreColor)) continue;   // painted the weld-ignore color in the blueprint
                            Vector3D center;
                            if (!IntersectsArea(b, ref areaObb, areaCenter, areaRadius, out center)) continue;
                            double key = SortKeyOf(center, ref areaCenter, ref areaInv, WorkPriorities.Rank(priority, WorkCategory.Projected));
                            if (!WouldKeep(key, WorkCategory.Projected, pool)) continue;
                            // Buildable right now (cell free, attached to built blocks): the cheap test first, it drops most
                            if (projector.CanBuild(b, false) != BuildCheckResult.OK) { unbuildable++; continue; }
                            found++;
                            PhysicalExtent(b, grid, out cell, out min, out max);
                            if (!HasFreeFace(grid, min, max, ref gravityUp)) { walledIn++; continue; }
                            // Not obstructed (physics test): only the few left
                            if (projector.CanBuild(b, true) != BuildCheckResult.OK) { obstructed++; continue; }
                            AddCandidate(b, projector, grid, Orchestrator.JobType.Weld, center, key, pool, WorkCategory.Projected);
                        }
                        _projectedBlocks.Clear();
                    }
                }
            }
            _areaGrids.Clear();

            // Build order: grid by grid (the one nearest the area centre first: candidates are sorted by distance)
            _planOrder.Clear();
            _planGrids.Clear();
            _planner.Reset();
            bool inGravity = gravityUp.LengthSquared() > 0.5;
            for (int i = 0; i < _candidates.Count; i++)
                if (!_planGrids.Contains(_candidates[i].Physical)) _planGrids.Add(_candidates[i].Physical);
            for (int g = 0; g < _planGrids.Count; g++)
            {
                var physical = _planGrids[g];
                _planItems.Clear();
                Vector3D top = TopOf(physical, gravityUp, areaMatrix.Up);
                for (int i = 0; i < _candidates.Count; i++)
                {
                    var c = _candidates[i];
                    if (c.Physical != physical) continue;
                    PhysicalExtent(c.Block, physical, out cell, out min, out max);
                    _planItems.Add(new BuildPlanner.Item { Tag = i, Min = min, Max = max, Projected = c.Projector != null,
                                                           Grind = c.Type == Orchestrator.JobType.Grind });
                }
                _planner.Plan(physical, _planItems, top, inGravity, _planOrder);
                for (int r = 0; r < _planner.Report.Count; r++) Log.Debug("Scan {0}: plan {1}", entityId, _planner.Report[r]);
            }
            _planItems.Clear();
            _planGrids.Clear();

            // Weld priority: the categories in their order, each in build order (a stable pass per rank)
            // (shares: first each kind up to its part of maxJobs, then what is left in priority order)
            int count = 0, noFace = 0;
            if (shares) SharesOf(priority, maxJobs, _outCap);
            for (int i = 0; i < _outCount.Length; i++) _outCount[i] = 0;
            _outDone.Clear();
            for (int round = shares ? 0 : 1; round < 2 && count < maxJobs; round++)
                for (int pass = 0, k = 0; pass < 3 && count < maxJobs; k++)
                {
                    if (k >= _planOrder.Count) { pass++; k = -1; continue; }
                    Candidate c = _candidates[_planOrder[k]];
                    if (WorkPriorities.Rank(priority, c.Category) != pass || _outDone.Contains(_planOrder[k])) continue;
                    int kind = KindOf(c.Category);
                    if (round == 0 && _outCount[kind] >= _outCap[kind]) continue;
                    _outDone.Add(_planOrder[k]);
                    ConstructionTarget target;
                    Vector3D topUp = TopOf(c.Physical, gravityUp, areaMatrix.Up);
                    if (!CreateTarget(ref c, ref areaCenter, ref gravityUp, ref topUp, out target)) { noFace++; continue; }   // no free face: unreachable
                    result.Add(target);
                    _outCount[kind]++;
                    count++;
                }
            _outDone.Clear();
            Log.Debug("Scan {0}: {1} grids{2}; {3} skipped; {4} projections in the area ({13} with nothing buildable, ignored); {5} candidates (pool {6}), "
                      + "skipped: {7} walled in, {8} projected not buildable yet, {14} projected obstructed, {9} without a face; {10} targets (max {11}); {12:F2} ms",
                      entityId, gridsScanned, gridText, refused, projectionsSeen, _candidates.Count, pool, walledIn, unbuildable, noFace,
                      count, maxJobs, Util.PerfStats.Since(scanStart), projectionsIdle, obstructed);
            _planOrder.Clear();
            _candidates.Clear();
            return count;
        }

        // A built block to weld: damaged (repair) or not welded up yet (unbuilt)
        private static WorkCategory CategoryOf(IMySlimBlock b, WorkModes modes)
        {
            return (modes & WorkModes.RepairDamagedBlocks) != 0 && (b.CurrentDamage > 0.0f || b.HasDeformation) ? WorkCategory.Repair : WorkCategory.Unbuilt;
        }

        /// <summary>A built block a scan would make a target of: welded / repaired by these modes, or grind-coloured.</summary>
        public static bool NeedsWork(IMySlimBlock b, WorkModes modes, Color grindColor, Color weldIgnoreColor)
        {
            if ((modes & WorkModes.Grind) != 0 && ColorUtil.SameColor(b, grindColor)) return true;
            return (modes & (WorkModes.WeldUnfinishedBlocks | WorkModes.RepairDamagedBlocks)) != 0 && NeedsWeld(b, modes)
                   && !ColorUtil.SameColor(b, weldIgnoreColor);
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

        // Weld priority rank first (the candidate pool keeps the higher categories), then the distance to the area centre
        private const double RANK_STRIDE = 1e12;
        private static double SortKeyOf(Vector3D center, ref Vector3D areaCenter, ref MatrixD areaInv, int rank)
        {
            return rank * RANK_STRIDE + Vector3D.TransformNormal(center - areaCenter, areaInv).LengthSquared();
        }

        // Candidates are kept sorted and capped at maxJobs (bounded insertion; System.Comparison is not whitelisted)
        private bool WouldKeep(double key, WorkCategory category, int maxJobs)
        {
            if (!_shares) return _candidates.Count < maxJobs || key < _candidates[_candidates.Count - 1].SortKey;
            int kind = KindOf(category);
            if (_poolCount[kind] < _poolCap[kind]) return true;
            int last = LastOfKind(kind);
            return last >= 0 && key < _candidates[last].SortKey;
        }

        #region Shares (construction computer)
        private bool _shares;
        private readonly int[] _poolCap = new int[4], _poolCount = new int[4], _outCap = new int[4], _outCount = new int[4];
        private readonly HashSet<int> _outDone = new HashSet<int>();

        // 0..2 the weld kinds (WorkCategory Repair / Projected / Unbuilt), 3 grind
        private static int KindOf(WorkCategory c)
        {
            return (int)c < 3 ? (int)c : 3;
        }

        // 'total' split 3 : 2 : 1 by weld priority rank, grind 3 (at least 1 each)
        private static void SharesOf(WorkPriority priority, int total, int[] caps)
        {
            int sum = 0;
            for (int k = 0; k < 4; k++) sum += k == 3 ? 3 : 3 - WorkPriorities.Rank(priority, (WorkCategory)k);
            for (int k = 0; k < 4; k++)
            {
                int w = k == 3 ? 3 : 3 - WorkPriorities.Rank(priority, (WorkCategory)k);
                caps[k] = Math.Max(1, (total * w + sum - 1) / sum);
            }
        }

        private int LastOfKind(int kind)
        {
            for (int i = _candidates.Count - 1; i >= 0; i--) if (KindOf(_candidates[i].Category) == kind) return i;
            return -1;
        }
        #endregion

        private void AddCandidate(IMySlimBlock b, IMyProjector projector, IMyCubeGrid physical, Orchestrator.JobType type, Vector3D center,
                                  double key, int maxJobs, WorkCategory category)
        {
            var c = new Candidate { Block = b, Projector = projector, Physical = physical, Type = type, Center = center, SortKey = key,
                                    Category = category };
            int kind = KindOf(category);
            if (_shares)
            {
                if (_poolCount[kind] >= _poolCap[kind])
                {
                    int last = LastOfKind(kind);
                    if (last >= 0) { _candidates.RemoveAt(last); _poolCount[kind]--; }
                }
            }
            else if (_candidates.Count >= maxJobs) { _poolCount[KindOf(_candidates[_candidates.Count - 1].Category)]--; _candidates.RemoveAt(_candidates.Count - 1); }
            int i = _candidates.Count;
            while (i > 0 && _candidates[i - 1].SortKey > key) i--;
            _candidates.Insert(i, c);
            _poolCount[kind]++;
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

        private bool CreateTarget(ref Candidate c, ref Vector3D areaCenter, ref Vector3D gravityUp, ref Vector3D topUp, out ConstructionTarget target)
        {
            target = default(ConstructionTarget);
            var b = c.Block;
            // Projections are tracked in the projector's grid: that's where the block will exist once built
            IMyCubeGrid physical = c.Projector != null ? c.Projector.CubeGrid : b.CubeGrid;
            Vector3I cell, min, max;
            PhysicalExtent(b, physical, out cell, out min, out max);

            Vector3I faceCell, normal;
            if (!ChooseFace(physical, min, max, areaCenter, gravityUp, 1, null, out faceCell, out normal, false, null, topUp)) return false;

            _missingCache.Clear();
            if (c.Type == Orchestrator.JobType.Weld)
            {
                if (c.Projector != null) AddDefinitionComponents(b, _missingCache);
                else b.GetMissingComponents(_missingCache);
            }
            else AddMountedComponentsForGrind(b, _missingCache);

            float top, side, bottom;
            FaceAccess(physical, min, max, normal, ref gravityUp, out top, out side, out bottom);
            target = new ConstructionTarget
            {
                AccessTop = top, AccessSide = side, AccessBottom = bottom,
                Components = new DiscreteInventory(_missingCache),
                Center = c.Center,
                GridEntityId = physical.EntityId,
                Cell = cell,
                FaceCell = faceCell,
                FaceNormal = normal,
                Type = c.Type,
                IsProjected = c.Projector != null,
                Category = c.Category,
                BlockName = b.BlockDefinition is MyCubeBlockDefinition ? ((MyCubeBlockDefinition)b.BlockDefinition).DisplayNameText : b.BlockDefinition.ToString(),
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
            IMyProjector projector;
            return TryGetBlockExtent(grid, cell, out min, out max, out projector);
        }

        /// <summary>Same; 'projector': the one projecting it (null: built / started).</summary>
        public static bool TryGetBlockExtent(IMyCubeGrid grid, Vector3I cell, out Vector3I min, out Vector3I max, out IMyProjector projector)
        {
            projector = null;
            Vector3I position;
            var slim = grid.GetCubeBlock(cell);
            if (slim != null)
            {
                PhysicalExtent(slim, grid, out position, out min, out max);
                return true;
            }
            Vector3D world = grid.GridIntegerToWorld(cell);
            foreach (var p in grid.GetFatBlocks<IMyProjector>())
            {
                var projected = p.IsProjecting ? p.ProjectedGrid : null;
                if (projected == null) continue;
                slim = projected.GetCubeBlock(projected.WorldToGridInteger(world));
                if (slim == null) continue;
                PhysicalExtent(slim, grid, out position, out min, out max);
                projector = p;
                return true;
            }
            min = max = cell;
            return false;
        }

        private static readonly List<IMyCubeGrid> _projectedGrids = new List<IMyCubeGrid>();

        /// <summary>
        /// Cells in the shell one cell around min..max that are projected (by a projector on 'grid') and not built yet:
        /// what a tool sphere working the block could build along with it.
        /// </summary>
        public static void ProjectedAround(IMyCubeGrid grid, Vector3I min, Vector3I max, List<Vector3I> into)
        {
            into.Clear();
            _projectedGrids.Clear();
            foreach (var p in grid.GetFatBlocks<IMyProjector>())
                if (p.IsProjecting && p.ProjectedGrid != null) _projectedGrids.Add(p.ProjectedGrid);
            if (_projectedGrids.Count == 0) return;
            Vector3I c;
            for (c.X = min.X - 1; c.X <= max.X + 1; c.X++)
                for (c.Y = min.Y - 1; c.Y <= max.Y + 1; c.Y++)
                    for (c.Z = min.Z - 1; c.Z <= max.Z + 1; c.Z++)
                    {
                        if (c.X >= min.X && c.X <= max.X && c.Y >= min.Y && c.Y <= max.Y && c.Z >= min.Z && c.Z <= max.Z) continue;
                        if (grid.CubeExists(c)) continue;
                        Vector3D world = grid.GridIntegerToWorld(c);
                        for (int i = 0; i < _projectedGrids.Count; i++)
                        {
                            var pg = _projectedGrids[i];
                            if (pg.GetCubeBlock(pg.WorldToGridInteger(world)) == null) continue;
                            into.Add(c);
                            break;
                        }
                    }
            _projectedGrids.Clear();
        }

        /// <summary>
        /// "Up" for building on 'grid': gravity; in space the up of a projector on the grid that is projecting (the
        /// ship being built), else 'areaUp' (the observation area's), else the grid's own up. One rule for the scan,
        /// the plan and the drone choosing its face, so they agree for the whole build.
        /// </summary>
        public static Vector3D TopOf(IMyCubeGrid grid, Vector3D gravityUp, Vector3D areaUp)
        {
            if (gravityUp.LengthSquared() > 0.5) return gravityUp;
            if (grid != null)
                foreach (var p in grid.GetFatBlocks<IMyProjector>())
                    if (p.IsProjecting) return p.WorldMatrix.Up;
            if (areaUp.LengthSquared() > 0.5) return areaUp;
            return grid != null ? grid.WorldMatrix.Up : Vector3D.Up;
        }

        #region Tool face weights (TODO/14: bids prefer drones whose tool faces the easy side)
        private const float BOTTOM_ACCESS_FACTOR = 0.25f;   // from below only when nothing else does (ChooseFace)

        /// <summary>
        /// Ease of access of a block by side, in gravity: free outer face cells above / beside / below it (see
        /// <see cref="ConstructionTarget.AccessTop"/>), the chosen face's kind counting double. All 0 in space.
        /// </summary>
        public static void FaceAccess(IMyCubeGrid grid, Vector3I min, Vector3I max, Vector3I chosenNormal, ref Vector3D gravityUp,
                                      out float top, out float side, out float bottom)
        {
            top = side = bottom = 0;
            if (gravityUp.LengthSquared() < 0.5) return;
            MatrixD m = grid.WorldMatrix;
            for (int i = 0; i < 6; i++)
            {
                Vector3I d = Base6Directions.GetIntVector((Base6Directions.Direction)i);
                Vector3I lo = min, hi = max;
                if (d.X > 0) lo.X = max.X; else if (d.X < 0) hi.X = min.X;
                if (d.Y > 0) lo.Y = max.Y; else if (d.Y < 0) hi.Y = min.Y;
                if (d.Z > 0) lo.Z = max.Z; else if (d.Z < 0) hi.Z = min.Z;
                int free = 0;
                Vector3I c;
                for (c.X = lo.X; c.X <= hi.X; c.X++)
                    for (c.Y = lo.Y; c.Y <= hi.Y; c.Y++)
                        for (c.Z = lo.Z; c.Z <= hi.Z; c.Z++)
                            if (!grid.CubeExists(c + d)) free++;
                AddAccess(Vector3D.Dot(Vector3D.TransformNormal((Vector3D)d, m), gravityUp), free, ref top, ref side, ref bottom);
            }
            float total = top + side + bottom;
            AddAccess(Vector3D.Dot(Vector3D.TransformNormal((Vector3D)chosenNormal, m), gravityUp), Math.Max(1f, total), ref top, ref side, ref bottom);
            total = top + side + bottom;
            if (total <= 0) return;
            top /= total; side /= total; bottom /= total;
        }

        private static void AddAccess(double up, float n, ref float top, ref float side, ref float bottom)
        {
            if (up > 0.7) top += n;
            else if (up < -0.7) bottom += n * BOTTOM_ACCESS_FACTOR;
            else side += n;
        }

        /// <summary>
        /// A job's tool face weights, index = Base6Directions.Direction of the tool in the drone's controller frame,
        /// 0..1 (the easiest kind 1). Null in space (or no access data): any tool orientation does.
        /// </summary>
        public static List<float> ToolFaceWeights(List<ConstructionTarget> targets)
        {
            float top = 0, side = 0, bottom = 0;
            for (int i = 0; i < targets.Count; i++) { top += targets[i].AccessTop; side += targets[i].AccessSide; bottom += targets[i].AccessBottom; }
            float max = Math.Max(top, Math.Max(side, bottom));
            if (max <= 0) return null;
            var w = new List<float>(6);
            for (int i = 0; i < 6; i++) w.Add(0);
            for (int i = 0; i < 4; i++) w[i] = side / max;                // Forward, Backward, Left, Right: from the sides
            w[(int)Base6Directions.Direction.Up] = bottom / max;           // tool up: from below
            w[(int)Base6Directions.Direction.Down] = top / max;            // tool down: from above
            return w;
        }
        #endregion

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
        /// - the top face first (TOP_FACE_WEIGHT: 'up' = gravity, else 'topUp', else the grid's up): blocks laid side
        ///   by side (a floor, a layer of a build) stay reachable from above whatever was welded around them; the
        ///   drone may come in at an angle (the work sphere sits on the face, see DispatchToolTo),
        /// - never from below in gravity (floors),
        /// - then outwards from the area centre (build inside first, keep the drone outside),
        /// - the line out along the normal ('clearCells' deep) and the cells around it free: built blocks there
        ///   count against the face (the drone would sit in them, or scrape them / the floor),
        /// - optional 'extraCost' per world normal (the drone attitude that side needs, which side it is on...),
        /// - ties broken at random.
        /// False when no face is free: the block can't be reached right now.
        /// </summary>
        /// <param name="normal">Unit axis of 'grid', pointing out of the face towards the tool.</param>
        /// <param name="lineMustBeClear">Faces with a built block anywhere on the line out ('clearCells') don't count.</param>
        /// <param name="reject">Faces (cell, normal) the caller rules out.</param>
        private const double TOP_FACE_WEIGHT = 2.0;
        private const double BOTTOM_FACE_PENALTY = 1.5;   // the underside only when no other face does

        public static bool ChooseFace(IMyCubeGrid grid, Vector3I min, Vector3I max, Vector3D areaCenter, Vector3D gravityUp,
                                      int clearCells, Func<Vector3D, double> extraCost, out Vector3I faceCell, out Vector3I normal,
                                      bool lineMustBeClear = false, Func<Vector3I, Vector3I, bool> reject = null,
                                      Vector3D topUp = default(Vector3D))
        {
            faceCell = min;
            normal = Vector3I.Zero;
            MatrixD m = grid.WorldMatrix;
            bool inGravity = gravityUp.LengthSquared() > 0.5;
            Vector3D top = inGravity ? gravityUp : topUp.LengthSquared() > 0.5 ? Vector3D.Normalize(topUp) : m.Up;
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
                double up = Vector3D.Dot(w, top);
                // The top first, then outwards; the underside last (a block whose only free face is its bottom - an
                // overhang - is reached from below; the way in is checked against the floor / terrain by the drone)
                double sideScore = TOP_FACE_WEIGHT * Math.Max(0, up)
                                 + 0.5 * Vector3D.Dot(w, outward)
                                 - (inGravity && up < -0.7 ? BOTTOM_FACE_PENALTY : 0);
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
                            if (reject != null && reject(c, d)) continue;       // ruled out by the caller (e.g. another grid in front)
                            double score = sideScore;
                            bool lineBlocked = false;
                            for (int k = 1; k <= clearCells && !lineBlocked; k++)
                            {
                                Vector3I p = c + d * k;
                                if (k > 1 && grid.CubeExists(p))
                                {
                                    if (lineMustBeClear) { lineBlocked = true; continue; }
                                    score -= 3;                                 // the hull would sit in a block
                                }
                                // Tight spots: blocks around the line (the floor worst: the hull hangs below the tool)
                                if (grid.CubeExists(p + e1)) score -= e1 == down ? 1 : 0.25;
                                if (grid.CubeExists(p - e1)) score -= -e1 == down ? 1 : 0.25;
                                if (grid.CubeExists(p + e2)) score -= e2 == down ? 1 : 0.25;
                                if (grid.CubeExists(p - e2)) score -= -e2 == down ? 1 : 0.25;
                            }
                            if (lineBlocked) continue;
                            score += _faceRandom.NextDouble() * 0.1;            // tie-break
                            if (score > bestScore) { bestScore = score; faceCell = c; normal = d; }
                        }
            }
            return normal != Vector3I.Zero;
        }

        /// <summary>
        /// Strafing between contiguous blocks: a face of the block min..max on the same side ('normal') and in the
        /// same plane as the face of 'fromCell', one cell away across the plane (a side neighbour preferred over a
        /// diagonal one), not covered, with its line out ('clearCells' deep) free. Diagonal moves also need both
        /// cells beside the path free. False when there is none: the drone backs out instead.
        /// </summary>
        public static bool FindAdjacentFace(IMyCubeGrid grid, Vector3I min, Vector3I max, Vector3I fromCell, Vector3I normal,
                                            int clearCells, out Vector3I faceCell)
        {
            faceCell = fromCell;
            Vector3I lo = min, hi = max;
            if (normal.X > 0) lo.X = max.X; else if (normal.X < 0) hi.X = min.X;
            if (normal.Y > 0) lo.Y = max.Y; else if (normal.Y < 0) hi.Y = min.Y;
            if (normal.Z > 0) lo.Z = max.Z; else if (normal.Z < 0) hi.Z = min.Z;
            // Same plane: the layer's coordinate along the normal equals the current face's
            int plane = lo.X * normal.X + lo.Y * normal.Y + lo.Z * normal.Z;
            if (plane != fromCell.X * normal.X + fromCell.Y * normal.Y + fromCell.Z * normal.Z) return false;
            int best = int.MaxValue;
            Vector3I c;
            for (c.X = lo.X; c.X <= hi.X; c.X++)
                for (c.Y = lo.Y; c.Y <= hi.Y; c.Y++)
                    for (c.Z = lo.Z; c.Z <= hi.Z; c.Z++)
                    {
                        Vector3I d = c - fromCell;
                        int ax = Math.Abs(d.X), ay = Math.Abs(d.Y), az = Math.Abs(d.Z);
                        if (Math.Max(ax, Math.Max(ay, az)) != 1) continue;          // not a neighbour across the plane
                        int manhattan = ax + ay + az;
                        if (manhattan >= best) continue;
                        if (grid.CubeExists(c + normal)) continue;                 // covered: not a face
                        bool clear = true;
                        for (int k = 2; k <= clearCells && clear; k++)
                            if (grid.CubeExists(c + normal * k)) clear = false;    // the drone would sit in a block
                        if (!clear) continue;
                        if (manhattan == 2)
                        {
                            // Diagonal: the two cells beside the path, in front of the plane, must be free
                            Vector3I a = new Vector3I(d.X, 0, 0), b = d - a;
                            if (a == Vector3I.Zero) { a = new Vector3I(0, d.Y, 0); b = d - a; }
                            if (grid.CubeExists(fromCell + a + normal) || grid.CubeExists(fromCell + b + normal)) continue;
                        }
                        best = manhattan;
                        faceCell = c;
                    }
            return best != int.MaxValue;
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
            double best = 0.6;   // approaches tilted up to ~50° from the normal (DispatchToolTo) still find their face
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
        /// Tasks: [load] + (NavigateToolTo a cell face, Weld/Grind, BackOut) per block + ReturnHome + Unload(everything). Positions are
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
                // (weld kind for logs: the one its blocks share, else unknown)
                byte weld = target.Type == Orchestrator.JobType.Weld ? Orchestrator.JobTypeExtensions.WeldCode(target.Category) : Orchestrator.JobTypeExtensions.WELD_ANY;
                if (job == null) { job = NewJob(target.Type); job.WeldCategory = weld; }
                else if (job.WeldCategory != weld) job.WeldCategory = Orchestrator.JobTypeExtensions.WELD_ANY;
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

        /// <summary>
        /// A job to announce (construction computer block): the work tasks (NavigateToolTo, Weld / Grind, BackOut) of
        /// 'targets' (one job type: the first target's; others are skipped), world positions; 'items' gets what they
        /// need (weld) / return (grind). Null when none of them exists any more.
        /// </summary>
        public Orchestrator.Job JobFor(List<ConstructionTarget> targets, float naturalGravity, DiscreteInventory items)
        {
            if (targets == null || targets.Count == 0) return null;
            var job = NewJob(targets[0].Type);
            if (job.JobType == Orchestrator.JobType.Weld) job.WeldCategory = Orchestrator.JobTypeExtensions.WeldCode(targets[0].Category);
            MatrixD am = MatrixD.Identity;
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                if (t.Type != job.JobType || !MyAPIGateway.Entities.EntityExists(t.GridEntityId)) continue;
                int before = job.BlockCount;
                AddWorkPair(job, ref t, null, ref am, naturalGravity, false);
                if (job.BlockCount > before && t.Components != null) items.AddItems(t.Components);
            }
            return job.BlockCount > 0 ? job : null;
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

        // NavigateToolTo + the tool task + BackOut for one block. 'partial': the drone carries only part of what the block
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
            var backOut = beacon != null ? Orchestrator.Task.BackOut(beacon.EntityId, ToFrame(ref am, approach))
                                         : Orchestrator.Task.BackOut(0, approach);
            var tool = target.Type == Orchestrator.JobType.Weld
                ? Orchestrator.Task.Weld(target.GridEntityId, target.Cell, target.IsProjected)
                : Orchestrator.Task.Grind(target.GridEntityId, target.Cell);
            // Small grid: the welder's sphere spans several blocks and welds the neighbours too, eating the parts
            // loaded for this one. Weld until the cargo has nothing more for it, never wait for full integrity.
            if (target.Type == Orchestrator.JobType.Weld && grid.GridSizeEnum == VRage.Game.MyCubeSize.Small)
                tool.Completion = Orchestrator.ToolTaskCompletionTrigger.DroneInventoryEmpty;
            if (partial)
                tool.Completion = target.Type == Orchestrator.JobType.Weld
                    ? Orchestrator.ToolTaskCompletionTrigger.DroneInventoryEmpty
                    : Orchestrator.ToolTaskCompletionTrigger.DroneInventoryFull;
            job.Tasks.Add(tool);
            job.Tasks.Add(backOut);   // straight back out along the normal before the next block
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
        /// <summary>Repair / projected / unbuilt (weld priority), or grind.</summary>
        public WorkCategory Category;
        /// <summary>The block's display name (logs).</summary>
        public string BlockName;
        /// <summary>Gravity only (0 in space): how easily the block is reached from above (tool facing down),
        /// from the sides (tool facing forward / back / left / right: a level drone turns about the vertical) and from
        /// below (tool facing up). Shares of 1: free outer faces of each kind, the chosen face counting double.</summary>
        public float AccessTop, AccessSide, AccessBottom;
    }
}
