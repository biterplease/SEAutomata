using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage;
using VRageMath;
using VRage.Game.ModAPI;
using ProtoBuf;
using System;

using Automata.Util;
using Automata.Inventory;
using InventoryClass = Automata.Inventory.Inventory;

namespace Automata.LogisticsComputer
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
    }

    public class InventoryFulfillmentData
    {
        [ProtoMember(1)]
        public List<IOLocationData> IOBlockPositions;
        [ProtoMember(2)]
        public InventoryClass Inventory;
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
        private readonly InventoryClass _inventoryCache = new InventoryClass();
        private readonly InventoryClass currentInventory = new InventoryClass();
        private readonly Dictionary<uint, InventoryClass> pendingInventoryByBidRoundBidId = new Dictionary<uint, InventoryClass>();
        private readonly Dictionary<uint, InventoryClass> reservedInventoryByBidRoundWinningBidId = new Dictionary<uint, InventoryClass>();
        private readonly Dictionary<uint, MyFixedPoint> pendingVolumeByBidRoundBidId = new Dictionary<uint, MyFixedPoint>();
        private readonly Dictionary<uint, MyFixedPoint> reservedVolumeByBidRoundWinningBidId = new Dictionary<uint, MyFixedPoint>();
        private readonly object _inventoryLock = new object();
        private readonly List<IOLocationData> ioLocationData = new List<IOLocationData>();
        public uint ConveyorNetworkId;
        public List<IMyShipConnector> connectors;
        public List<IMyCollector> collectors;
        public List<IMyCargoContainer> cargoContainers;
        public List<IMyAssembler> assemblers;
        public List<IMyRefinery> refineries;
        public List<IMyReactor> reactors;
        public InventoryClass InventoryClass;

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
            _sessionDelegate = sessionDelegate ?? new MySessionDelegate();
        }

        public void UpdateInventories()
        {
            var currentFrame = _sessionDelegate.GameplayFrameCounter;
            // if (currentFrame == _lastInventoryUpdateFrame)
            // {
            //     return;
            // }
            _isUpdating = true;
            MyFixedPoint cargoMaxVolume = MyFixedPoint.Zero;
            MyFixedPoint cargoCurrentVolume = MyFixedPoint.Zero;
            List<IMyCubeBlock> containers = new List<IMyCubeBlock>();
            foreach (IMyCargoContainer b in cargoContainers)
            {
                if (b == null || !b.IsFunctional)
                {
                    continue;
                }
                containers.Add(b);
                IMyInventory cargoInv = b.GetInventory();
                if (cargoInv != null)
                {
                    cargoMaxVolume += cargoInv.MaxVolume;
                    cargoCurrentVolume += cargoInv.CurrentVolume;
                }
            }
            foreach (IMyShipConnector b in connectors)
            {
                if (b != null) containers.Add(b);
            }
            foreach (IMyAssembler b in assemblers)
            {
                if (b != null) containers.Add(b);
            }
            foreach (IMyCollector b in collectors)
            {
                if (b != null) containers.Add(b);
            }
            foreach (IMyRefinery b in refineries)
            {
                if (b != null) containers.Add(b);
            }
            foreach (IMyReactor b in reactors)
            {
                if (b != null) containers.Add(b);
            }

            lock (_inventoryLock)
            {
                _maxVolume = cargoMaxVolume;
                _currentVolume = cargoCurrentVolume;
                _inventoryCache.Clear();
                foreach (IMyCubeBlock container in containers)
                {
                    if (container != null)
                    {
                        _inventoryCache.AddItems(container);
                    }
                }
                // Substract returns a new InventoryClass and does not mutate this; apply chain then copy into currentInventory.
                var afterPending = InventoryClass.Substract(_inventoryCache, GetPendingInventory());
                var afterReserved = InventoryClass.Substract(afterPending, GetReservedInventory());
                currentInventory.Clear();
                foreach (KVPair kv in afterReserved.GetAllItems())
                {
                    currentInventory.AddItem(kv.Key, kv.Value);
                }
            }
            _isUpdating = false;
            _lastInventoryUpdateFrame = currentFrame;
        }

        /// <summary>
        /// Returns the current inventory, should reflect the actual count of items.
        /// </summary>
        /// <returns></returns>
        public InventoryClass GetCurrentInventory()
        {
            return currentInventory;
        }

        /// <summary>
        /// Reserved inventory represents items that have been proposed as a bid, and the bid has been won.
        /// </summary>
        /// <returns></returns>
        public InventoryClass GetReservedInventory()
        {
            InventoryClass reservedInventory = new InventoryClass();
            foreach (KeyValuePair<uint, InventoryClass> kv in reservedInventoryByBidRoundWinningBidId)
            {
                reservedInventory.AddItems(kv.Value);
            }
            return reservedInventory;
        }

        /// <summary>
        /// Pending inventory represents items that have been proposed as a bid, but not confirmed yet.
        /// </summary>
        /// <returns></returns>
        public InventoryClass GetPendingInventory()
        {
            InventoryClass pendingInventory = new InventoryClass();
            foreach (KeyValuePair<uint, InventoryClass> kv in pendingInventoryByBidRoundBidId)
            {
                pendingInventory.AddItems(kv.Value);
            }
            return pendingInventory;
        }

        /// <summary>
        /// Returns the available inventory, showing current - reserved.
        /// </summary>
        /// <returns></returns>
        public InventoryClass AvailableInventory()
        {
            return InventoryClass.Substract(currentInventory, GetReservedInventory());
        }

        /// <summary>
        /// Free cargo volume (liters) from game inventory meters, based on cargo containers only.
        /// Compare to <see cref="InventoryClass.TotalInventoryVolume"/> for incoming stacks.
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

        public InventoryFulfillmentData CanFulfillRequest(InventoryClass requestedInventory, uint bidRoundBidId)
        {
            lock (_inventoryLock)
            {
                if (AvailableInventory().ContainsAtLeast(requestedInventory))
                {
                    InventoryClass reservedInventory = new InventoryClass();
                    InventoryClass.MoveItems(currentInventory, reservedInventory, requestedInventory);
                    pendingInventoryByBidRoundBidId[bidRoundBidId] = reservedInventory;
                    return new InventoryFulfillmentData
                    {
                        InventoryClass = requestedInventory,
                        Fulfillment = InventoryFulfillment.SatisfyFully,
                        IOBlockPositions = ioLocationData,
                        ConveyorNetworkId = ConveyorNetworkId
                    };
                }
                InventoryClass fulfillablePart;
                AvailableInventory().FilterByKeys(requestedInventory, out fulfillablePart);
                pendingInventoryByBidRoundBidId[bidRoundBidId] = fulfillablePart;
                return new InventoryFulfillmentData
                {
                    InventoryClass = fulfillablePart,
                    Fulfillment = InventoryFulfillment.SatisfyPartial,
                    IOBlockPositions = FilterIOByType(ioLocationData, IOBlockType.ShipConnector),
                    ConveyorNetworkId = ConveyorNetworkId
                };
            }
        }
        public VolumeAcceptanceData CanAcceptRequest(InventoryClass incomingInventory, uint bidRoundBidId)
        {
            lock (_inventoryLock)
            {
                MyFixedPoint incomingVolume = incomingInventory.TotalInventoryVolume();
                MyFixedPoint remainingCargoVolume = GetRemainingCargoVolume();
                if (incomingVolume <= remainingCargoVolume)
                {
                    pendingVolumeByBidRoundBidId[bidRoundBidId] = incomingVolume;
                    return new VolumeAcceptanceData
                    {
                        Volume = incomingVolume,
                        Fulfillment = InventoryFulfillment.AcceptAll,
                        IOBlockPositions = ioLocationData
                    };
                }
                MyFixedPoint fulfillableVolume = remainingCargoVolume - incomingVolume;
                return new VolumeAcceptanceData
                {
                    Volume = fulfillableVolume,
                    Fulfillment = InventoryFulfillment.AcceptPartial,
                    IOBlockPositions = ioLocationData
                };
            }
        }
        /// <summary>
        /// Drop a pending inventory entry, returning it to the current inventory.
        /// This should be called when a bid is placed, but not won.
        /// </summary>
        /// <param name="bidRoundBidId"></param>
        public void DropPendingInventoryEntry(uint bidRoundBidId)
        {
            lock (_inventoryLock)
            {
                InventoryClass pending;
                if (!pendingInventoryByBidRoundBidId.TryGetValue(bidRoundBidId, out pending))
                {
                    return;
                }
                InventoryClass.MoveItems(pending, currentInventory, pending);
                pendingInventoryByBidRoundBidId.Remove(bidRoundBidId);
            }
        }
        /// <summary>
        /// Cancel a reservation, returning the inventory to the current inventory.
        /// This should be called when a winning bid is cancelled; maybe the drone was destroyed,
        /// the inventory was destroyed or the player took the inventory.
        /// </summary>
        /// <param name="bidRoundBidId"></param>
        public void CancelInventoryReservation(uint bidRoundBidId)
        {
            lock (_inventoryLock)
            {
                InventoryClass reserved;
                if (!reservedInventoryByBidRoundWinningBidId.TryGetValue(bidRoundBidId, out reserved))
                {
                    return;
                }
                InventoryClass.MoveItems(reserved, currentInventory, reserved);
                reservedInventoryByBidRoundWinningBidId.Remove(bidRoundBidId);
            }
        }

        /// <summary>
        /// Drop a pending volume entry, returning it to the current inventory.
        /// This should be called when a volume-accepting bid is placed, but not won.
        /// </summary>
        /// <param name="bidRoundBidId"></param>
        public void DropPendingVolumeEntry(uint bidRoundBidId)
        {
            lock (_inventoryLock)
            {
                MyFixedPoint pendingVolume;
                if (!pendingVolumeByBidRoundBidId.TryGetValue(bidRoundBidId, out pendingVolume))
                {
                    return;
                }
                pendingVolumeByBidRoundBidId.Remove(bidRoundBidId);
            }
        }

             /// <summary>
        /// Cancel a reservation, returning the inventory to the current inventory.
        /// This should be called when a winning bid is cancelled; maybe the drone was destroyed,
        /// the inventory was destroyed or the player took the inventory.
        /// </summary>
        /// <param name="bidRoundBidId"></param>
        public void CancelVolumeReservation(uint bidRoundBidId)
        {
            lock (_inventoryLock)
            {
                MyFixedPoint pendingVolume;
                if (!reservedVolumeByBidRoundWinningBidId.TryGetValue(bidRoundBidId, out pendingVolume))
                {
                    return;
                }
                reservedVolumeByBidRoundWinningBidId.Remove(bidRoundBidId);
            }
        }

        public void ReserveInventory(uint bidRoundBidId)
        {
            lock (_inventoryLock)
            {
                InventoryClass pendingToBeReserved;
                if (!pendingInventoryByBidRoundBidId.TryGetValue(bidRoundBidId, out pendingToBeReserved))
                {
                    return;
                }
                reservedInventoryByBidRoundWinningBidId[bidRoundBidId] = pendingToBeReserved;
                // remove from pending
                pendingInventoryByBidRoundBidId.Remove(bidRoundBidId);
            }
        }

        public void ReserveVolume(uint bidRoundBidId)
        {
            lock (_inventoryLock)
            {
                MyFixedPoint pendingVolume;
                if (!pendingVolumeByBidRoundBidId.TryGetValue(bidRoundBidId, out pendingVolume))
                {
                    return;
                }
                reservedVolumeByBidRoundWinningBidId[bidRoundBidId] = pendingVolume;
                pendingVolumeByBidRoundBidId.Remove(bidRoundBidId);
            }
        }


        /// <summary>
        /// Release the reserved inventory. Called when inventory is picked up.
        /// </summary>
        /// <param name="bidRoundBidId"></param>
        public void ReleaseInventory(uint bidRoundBidId)
        {
            lock (_inventoryLock)
            {
                InventoryClass reserved;
                if (!reservedInventoryByBidRoundWinningBidId.TryGetValue(bidRoundBidId, out reserved))
                {
                    return;
                }
                reservedInventoryByBidRoundWinningBidId.Remove(bidRoundBidId);
            }
        }

       /// <summary>
        /// Release the reserved volume. Called when incoming inventory is accepted.
        /// </summary>
        /// <param name="bidRoundBidId"></param>
        public void ReleaseVolume(uint bidRoundBidId)
        {
            lock (_inventoryLock)
            {
                MyFixedPoint reservedVolume;
                if (!reservedVolumeByBidRoundWinningBidId.TryGetValue(bidRoundBidId, out reservedVolume))
                {
                    return;
                }
                reservedInventoryByBidRoundWinningBidId.Remove(bidRoundBidId);
            }
        }

        public void UpdateIOLocationData()
        {
            ioLocationData.Clear();
            foreach (var connector in connectors)
            {
                if (connector.IsFunctional && connector.IsWorking)
                {
                    Quaternion q;
                    connector.Orientation.GetQuaternion(out q);
                    ioLocationData.Add(new IOLocationData()
                    {
                        PositionData = Vector3DData.FromVector3D(connector.GetPosition()),
                        OrientationData = QuaternionDData.FromQuaternion(q),
                        BlockType = IOBlockType.ShipConnector
                    });
                }
            }
            foreach (var collector in collectors)
            {
                if (collector.IsFunctional && collector.IsWorking)
                {
                    Quaternion q;
                    collector.Orientation.GetQuaternion(out q);
                    ioLocationData.Add(new IOLocationData()
                    {
                        PositionData = Vector3DData.FromVector3D(collector.GetPosition()),
                        OrientationData = QuaternionDData.FromQuaternion(q),
                        BlockType = IOBlockType.Collector
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