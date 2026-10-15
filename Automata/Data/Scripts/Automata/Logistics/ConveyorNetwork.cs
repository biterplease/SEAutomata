using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage;
using VRageMath;
using VRage.Game.ModAPI;
using ProtoBuf;
using System;

using VRage.Game.Entity;

using Automata.Util;
using Automata.Inventory;

namespace Automata.Logistics
{
    public enum IOBlockType : byte
    {
        [ProtoEnum]
        ShipConnector = 1,
        [ProtoEnum]
        Collector = 2,
    }

    [Serializable, ProtoContract(UseProtoMembersOnly = true)]
    public class IOLocationData
    {
        [ProtoMember(1)]
        public Vector3DData PositionData;
        [ProtoMember(2)]
        public QuaternionDData OrientationData;
        [ProtoMember(4)]
        public IOBlockType BlockType;
        /// <summary>The connector / collector itself (a drone resolves it, ownership-checked, to dock).</summary>
        [ProtoMember(5)]
        public long EntityId;
    }

    public class InventoryFulfillmentData
    {
        [ProtoMember(1)]
        public List<IOLocationData> IOBlockPositions;
        [ProtoMember(2)]
        public DiscreteInventory Inventory;
        [ProtoMember(3)]
        public uint ConveyorNetworkId; 
        [ProtoMember(4)]
        public InventoryFulfillment Fulfillment;
    }


    public class VolumeAcceptanceData
    {
        [ProtoMember(1)]
        public List<IOLocationData> IOBlockPositions;
        [ProtoMember(2)]
        public MyFixedPoint Volume;
        [ProtoMember(3)]
        public InventoryFulfillment Fulfillment;
    }

    public class ConveyorNetwork : IComparable<ConveyorNetwork>
    {
        /// <summary>
        ///  Beacon that this conveyor network is relative to
        /// </summary>
        public IMyBeacon RelativeBeacon;
        private readonly DiscreteInventory _inventoryCache = new DiscreteInventory();
        private readonly DiscreteInventory currentInventory = new DiscreteInventory();
        private readonly DiscreteInventory heldInventory = new DiscreteInventory();   // all of it, pullable or not
        private readonly List<IOLocationData> ioLocationData = new List<IOLocationData>();
        public uint ConveyorNetworkId;
        public List<IMyShipConnector> connectors;
        public List<IMyCollector> collectors;
        public List<IMyCargoContainer> cargoContainers;
        public List<IMyAssembler> assemblers;
        public List<IMyRefinery> refineries;
        public List<IMyReactor> reactors;
        /// <summary>O2/H2 generators (they take Ice from the network's cargo).</summary>
        public List<IMyGasGenerator> gasGenerators;
        /// <summary>Oxygen and hydrogen tanks (their inventories hold bottles).</summary>
        public List<IMyGasTank> gasTanks;
        public DiscreteInventory Inventory;

        // player managed properties, set from the IAILogisticsComputer terminal controls
        public string Nickname { get; set; }
        private int _priority;

        /// <summary>Sum of <see cref="IMyInventory.CurrentVolume"/> across functional cargo containers only.</summary>
        private MyFixedPoint _currentVolume = MyFixedPoint.Zero;
        public MyFixedPoint CurrentVolume => _currentVolume;
        /// <summary>Sum of <see cref="IMyInventory.MaxVolume"/> across functional cargo containers only.</summary>
        private MyFixedPoint _maxVolume = MyFixedPoint.Zero;
        public MyFixedPoint MaxVolume => _maxVolume;

        private long _lastInventoryUpdateFrame = 0;
        public long LastInventoryUpdateFrame => _lastInventoryUpdateFrame;
        private bool _isUpdating = false;
        public bool IsUpdating => _isUpdating;
        private IMySessionDelegate _sessionDelegate;

        public ConveyorNetwork(IMySessionDelegate sessionDelegate = null)
        {
            connectors = new List<IMyShipConnector>();
            collectors = new List<IMyCollector>();
            cargoContainers = new List<IMyCargoContainer>();
            assemblers = new List<IMyAssembler>();
            refineries = new List<IMyRefinery>();
            reactors = new List<IMyReactor>();
            gasGenerators = new List<IMyGasGenerator>();
            gasTanks = new List<IMyGasTank>();
            _sessionDelegate = sessionDelegate ?? new MySessionDelegate();
        }
        public void Clear()
        {
            connectors.Clear();
            collectors.Clear();
            cargoContainers.Clear();
            assemblers.Clear();
            refineries.Clear();
            reactors.Clear();
            gasGenerators.Clear();
            gasTanks.Clear();
        }

        #region Inventory totals
        // Event-driven totals (Logistics Computer networks, after Attach): one cached DiscreteInventory per inventory,
        // read again only when the game reports its contents changed (MyInventoryBase.ContentsChanged), plus a full
        // re-read every FULL_RESYNC_TICKS (paths that raise no event, a functional change, a missed subscription). Not
        // attached (the drone's home network): every inventory is read on each UpdateInventories.
        private const int FULL_RESYNC_TICKS = 60 * 60;

        private sealed class TrackedInventory
        {
            public IMyCubeBlock Block;
            public int Index;
            public bool Pullable;
            public MyInventoryBase Inventory;
            public readonly DiscreteInventory Items = new DiscreteInventory();
            public bool Dirty = true;
            public Action<MyInventoryBase> OnChanged;
        }

        private readonly List<TrackedInventory> tracked = new List<TrackedInventory>();
        private readonly List<IMyCubeBlock> containerScratch = new List<IMyCubeBlock>();
        private bool attached;
        private bool totalsDirty = true;
        private long lastFullResync = long.MinValue / 2;

        /// <summary>Contents tracked by events (Attach).</summary>
        public bool Attached { get { return attached; } }

        /// <summary>
        /// Subscribes to the contents of every inventory of the network (after the blocks are sorted in). The owner calls
        /// Detach before dropping the network or mapping again.
        /// </summary>
        public void Attach()
        {
            Detach();
            CollectContainers(containerScratch, false);
            for (int c = 0; c < containerScratch.Count; c++)
            {
                var block = containerScratch[c];
                if (block == null || block.Closed || !block.HasInventory) continue;
                for (int i = 0; i < block.InventoryCount; i++)
                {
                    var inv = block.GetInventory(i) as MyInventoryBase;
                    if (inv == null) continue;
                    var t = new TrackedInventory { Block = block, Index = i, Pullable = IsPullable(block, i), Inventory = inv };
                    t.OnChanged = changed => { t.Dirty = true; totalsDirty = true; };
                    inv.ContentsChanged += t.OnChanged;
                    tracked.Add(t);
                }
            }
            containerScratch.Clear();
            attached = true;
            totalsDirty = true;
        }

        /// <summary>Drops the content subscriptions (Attach).</summary>
        public void Detach()
        {
            for (int i = 0; i < tracked.Count; i++)
                if (tracked[i].Inventory != null) tracked[i].Inventory.ContentsChanged -= tracked[i].OnChanged;
            tracked.Clear();
            attached = false;
        }

        // The network's item-holding blocks (cargo: functional only when 'functionalCargo')
        private void CollectContainers(List<IMyCubeBlock> containers, bool functionalCargo)
        {
            containers.Clear();
            foreach (IMyCargoContainer b in cargoContainers) if (b != null && (!functionalCargo || b.IsFunctional)) containers.Add(b);
            foreach (IMyShipConnector b in connectors) if (b != null) containers.Add(b);
            foreach (IMyCollector b in collectors) if (b != null) containers.Add(b);
            foreach (IMyAssembler b in assemblers) if (b != null) containers.Add(b);
            foreach (IMyRefinery b in refineries) if (b != null) containers.Add(b);
            foreach (IMyReactor b in reactors) if (b != null) containers.Add(b);
            foreach (IMyGasGenerator b in gasGenerators) if (b != null) containers.Add(b);
            foreach (IMyGasTank b in gasTanks) if (b != null) containers.Add(b);
        }

        public void UpdateInventories()
        {
            var currentFrame = _sessionDelegate.GameplayFrameCounter;
            _isUpdating = true;
            // Cargo volumes: property reads, every call (a damaged / repaired container raises no content event)
            MyFixedPoint cargoMaxVolume = MyFixedPoint.Zero;
            MyFixedPoint cargoCurrentVolume = MyFixedPoint.Zero;
            foreach (IMyCargoContainer b in cargoContainers)
            {
                if (b == null || !b.IsFunctional) continue;
                IMyInventory cargoInv = b.GetInventory();
                if (cargoInv == null) continue;
                cargoMaxVolume += cargoInv.MaxVolume;
                cargoCurrentVolume += cargoInv.CurrentVolume;
            }
            _maxVolume = cargoMaxVolume;
            _currentVolume = cargoCurrentVolume;
            // Every block of the network that holds items is accounted for (held); only the inventories a drone may
            // take from count as stock to supply (IsPullable): never ice out of O2/H2 generators, a refinery's or an
            // assembler's input, a reactor's fuel, a tank's bottles
            if (attached) SumTracked(currentFrame); else ReadAll();
            _isUpdating = false;
            _lastInventoryUpdateFrame = currentFrame;
        }

        // Attached: only the inventories that changed are read; the totals are summed from the per-inventory caches
        private void SumTracked(long currentFrame)
        {
            if (currentFrame - lastFullResync >= FULL_RESYNC_TICKS)
            {
                lastFullResync = currentFrame;
                for (int i = 0; i < tracked.Count; i++) tracked[i].Dirty = true;
                totalsDirty = true;
            }
            if (!totalsDirty) return;
            totalsDirty = false;
            _inventoryCache.Clear();
            heldInventory.Clear();
            for (int i = 0; i < tracked.Count; i++)
            {
                var t = tracked[i];
                if (t.Block == null || t.Block.Closed) continue;
                if (t.Block is IMyCargoContainer && !t.Block.IsFunctional) { t.Dirty = true; continue; }   // as ReadAll: broken cargo doesn't count
                if (t.Dirty)
                {
                    t.Items.Clear();
                    t.Items.AddItems(t.Block.GetInventory(t.Index));
                    t.Dirty = false;
                }
                heldInventory.AddItems(t.Items);
                if (t.Pullable) _inventoryCache.AddItems(t.Items);
            }
            currentInventory.Clear();
            currentInventory.AddItems(_inventoryCache);
        }

        // Not attached: every inventory read now
        private void ReadAll()
        {
            CollectContainers(containerScratch, true);
            _inventoryCache.Clear();
            heldInventory.Clear();
            for (int c = 0; c < containerScratch.Count; c++)
            {
                IMyCubeBlock container = containerScratch[c];
                if (container == null || container.Closed || !container.HasInventory) continue;
                for (int i = 0; i < container.InventoryCount; i++)
                {
                    IMyInventory inv = container.GetInventory(i);
                    if (inv == null) continue;
                    heldInventory.AddItems(inv);
                    if (IsPullable(container, i)) _inventoryCache.AddItems(inv);
                }
            }
            containerScratch.Clear();
            currentInventory.Clear();
            currentInventory.AddItems(_inventoryCache);
        }
        #endregion

        /// <summary>
        /// Returns the current inventory, should reflect the actual count of items.
        /// </summary>
        /// <returns></returns>
        public DiscreteInventory GetCurrentInventory()
        {
            return currentInventory;
        }

        /// <summary>
        /// Everything the network's blocks hold, whether a drone may take it or not (generators' ice, refinery /
        /// assembler inputs, reactor fuel, tank bottles): quotas and displays. Supply bids use <see cref="GetCurrentInventory"/>.
        /// </summary>
        public DiscreteInventory GetHeldInventory()
        {
            return heldInventory;
        }

        /// <summary>
        /// May items be taken out of this inventory of this block (supply, drone loads)? Cargo, connectors, collectors:
        /// yes. Assemblers (survival kits too) and refineries: the output (1) only - never their input. Reactors, O2/H2
        /// generators, gas tanks, anything else: no (fuel, ice, bottles stay where they are).
        /// </summary>
        public static bool IsPullable(IMyCubeBlock block, int inventoryIndex)
        {
            if (block is IMyCargoContainer || block is IMyShipConnector || block is IMyCollector) return inventoryIndex == 0;
            if (block is IMyAssembler || block is IMyRefinery) return inventoryIndex == 1;
            return false;
        }

        /// <summary>
        /// Free cargo volume (liters) from game inventory meters, based on cargo containers only.
        /// Compare to <see cref="DiscreteInventory.TotalInventoryVolume"/> for incoming stacks.
        /// </summary>
        public MyFixedPoint GetRemainingCargoVolume()
        {
            MyFixedPoint free = _maxVolume - _currentVolume;
            if (free < MyFixedPoint.Zero)
            {
                return MyFixedPoint.Zero;
            }
            return free;
        }

        public List<IOLocationData> GetIOBlockPositions()
        {
            return ioLocationData;
        }

        /// <summary>A (working) O2/H2 generator on the network: where Ice is worth delivering.</summary>
        public bool HasGasGenerators(bool working = true)
        {
            for (int i = 0; i < gasGenerators.Count; i++)
            {
                var g = gasGenerators[i];
                if (g != null && !g.Closed && (!working || g.IsWorking)) return true;
            }
            return false;
        }

        /// <summary>Oxygen tanks (true) or hydrogen tanks (false) on the network.</summary>
        public int GasTankCount(bool oxygen)
        {
            int n = 0;
            for (int i = 0; i < gasTanks.Count; i++)
            {
                var t = gasTanks[i];
                if (t == null || t.Closed) continue;
                bool h2 = t.BlockDefinition.SubtypeName.IndexOf("Hydrogen", StringComparison.OrdinalIgnoreCase) >= 0;
                if (h2 != oxygen) n++;
            }
            return n;
        }

        public bool HasRefineries(bool working = false)
        {
            if (!working)
                return refineries.Count > 0;
            foreach (IMyRefinery refinery in refineries)
            {
                if (refinery.IsFunctional && refinery.IsWorking)
                {
                    return true;
                }
            }
            return false;
        }
        public bool HasAssemblers(bool working = false)
        {
            if (!working)
            {
                return assemblers.Count > 0;
            }
            foreach (IMyAssembler assembler in assemblers)
            {
                if (assembler.IsFunctional && assembler.IsWorking)
                {
                    return true;
                }
            }
            return false;
        }

        public void UpdateIOLocationData()
        {
            ioLocationData.Clear();
            foreach (var connector in connectors)
            {
                // Free connectors only: a drone has to be able to dock there
                if (connector.IsFunctional && connector.IsWorking && !connector.IsConnected)
                {
                    ioLocationData.Add(new IOLocationData()
                    {
                        PositionData = Vector3DData.FromVector3D(connector.GetPosition()),
                        // world orientation (Orientation.GetQuaternion is grid-local)
                        OrientationData = QuaternionDData.FromQuaternionD(QuaternionD.CreateFromRotationMatrix(connector.WorldMatrix)),
                        BlockType = IOBlockType.ShipConnector,
                        EntityId = connector.EntityId,
                    });
                }
            }
            foreach (var collector in collectors)
            {
                if (collector.IsFunctional && collector.IsWorking)
                {
                    ioLocationData.Add(new IOLocationData()
                    {
                        PositionData = Vector3DData.FromVector3D(collector.GetPosition()),
                        OrientationData = QuaternionDData.FromQuaternionD(QuaternionD.CreateFromRotationMatrix(collector.WorldMatrix)),
                        BlockType = IOBlockType.Collector,
                        EntityId = collector.EntityId,
                    });
                }
            }
        }

        public int Priority {
            get{
                return _priority;
            }
            set
            {
                _priority = value;
            }
        }

        public int CompareTo(ConveyorNetwork other)
        {
            if (other == null) return 1;
            var comp =  _priority.CompareTo(other._priority);
            if (comp == 0 ){
                return CompareNicknamesOrdinal(Nickname, other.Nickname);
            }
            return comp;
        }

        private static int CompareNicknamesOrdinal(string left, string right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left == null)
            {
                return -1;
            }

            if (right == null)
            {
                return 1;
            }

            int minLength = left.Length < right.Length ? left.Length : right.Length;
            for (int i = 0; i < minLength; i++)
            {
                int charComparison = left[i].CompareTo(right[i]);
                if (charComparison != 0)
                {
                    return charComparison;
                }
            }

            return left.Length.CompareTo(right.Length);
        }

        // Vanilla small connectors (they only mate with small connectors)
        private static readonly HashSet<string> SmallConnectorSubtypes = new HashSet<string>
        {
            "ConnectorSmall",                   // small grid, 1x1x1
            "LargeBlockInsetConnectorSmall",    // large grid "small connector"
            "SmallBlockInsetConnector",         // small grid inset, 1x1x1
        };

        /// <summary>
        /// A standard (large) connector, as opposed to a small one: they only mate with their own size (TODO/14). Small:
        /// the vanilla subtypes, a model whose connector dummy is "small_connector", and (modded) a small-grid connector of
        /// a single cell.
        /// </summary>
        public static bool IsStandardConnector(IMyShipConnector c)
        {
            if (c == null) return false;
            if (SmallConnectorSubtypes.Contains(c.BlockDefinition.SubtypeName)) return false;
            if (Drone.DroneControllerBlock.IsSmallConnector(c)) return false;
            return !(c.CubeGrid != null && c.CubeGrid.GridSizeEnum == VRage.Game.MyCubeSize.Small && c.Min == c.Max);
        }

        public static bool IsSmallConnector(IMyShipConnector c)
        {
            return c != null && !IsStandardConnector(c);
        }

        public static void CategorizeBlock(ConveyorNetwork network, IMyCubeBlock block)
        {
            // Use 'as' and null check for C# 6.0
            var cargo = block as IMyCargoContainer;
            if (cargo != null)
            {
                network.cargoContainers.Add(cargo);
                return;
            }

            var connector = block as IMyShipConnector;
            if (connector != null)
            {
                network.connectors.Add(connector);
                return;
            }

            var collector = block as IMyCollector;
            if (collector != null)
            {
                network.collectors.Add(collector);
                return;
            }

            var assembler = block as IMyAssembler;
            if (assembler != null)
            {
                network.assemblers.Add(assembler);
                return;
            }

            var refinery = block as IMyRefinery;
            if (refinery != null)
            {
                network.refineries.Add(refinery);
                return;
            }

            var generator = block as IMyGasGenerator;
            if (generator != null)
            {
                network.gasGenerators.Add(generator);
                return;
            }

            var tank = block as IMyGasTank;
            if (tank != null)
            {
                network.gasTanks.Add(tank);
                return;
            }

            var reactor = block as IMyReactor;
            if (reactor != null)
            {
                network.reactors.Add(reactor);
                return;
            }
        }
        public static List<IOLocationData> FilterIOByType(List<IOLocationData> ioLocationData, IOBlockType blockType)
        {
            List<IOLocationData> result = new List<IOLocationData>();
            for (int i = 0; i < ioLocationData.Count; i++)
            {
                IOLocationData io = ioLocationData[i];
                if (io.BlockType == blockType)
                {
                    result.Add(io);
                }
            }
            return result;
        }
    }
}