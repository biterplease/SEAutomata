using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using VRage.Collections;

using Automata.VirtualNetwork;
using Automata.Inventory;
using InventoryClass = Automata.Inventory.Inventory;
using Automata.Util;
using Automata.Util.Logging;

namespace Automata.LogisticsComputer
{
    public class LogisticsComputer
    {
        private readonly long entityId;
        private readonly IMyEntity Entity;
        private readonly MessageQueue messaging;

        // Components
        private List<IMyCargoContainer> cargoContainers = new List<IMyCargoContainer>();
        private List<IMyShipConnector> connectors = new List<IMyShipConnector>();
        private List<IMyCollector> collectors = new List<IMyCollector>();
        private List<IMyAssembler> assemblers = new List<IMyAssembler>();
        private List<IMyRefinery> refineries = new List<IMyRefinery>();
        private List<IMyReactor> reactors = new List<IMyReactor>();
        private IMyRadioAntenna primaryAntenna;

        // State
        private State currentState = State.Initializing;
        private OperationMode operationMode;
        private bool isInitialized = false;
        private long lastUpdateFrame = 0;
        private long lastInventoryScanFrame = 0;
        private long lastConnectorCheckFrame = 0;
        private long lastPushCheckFrame = 0;


        private readonly List<Message<Auction>> auctionInbox = new List<Message<Auction>>();
        private readonly Queue<Message<Bid>> bidOutbox = new Queue<Message<Bid>>();

        private readonly List<ConveyorNetwork> conveyorNetworks = new List<ConveyorNetwork>();
        private int _conveyorNetworkUpdateIntervalTicks = 60;
        private long _lastConveyorNetworkUpdateFrame = 0;
        private readonly Dictionary<uint, long> conveyorNetworkLastUpdateFrame = new Dictionary<uint, long>();
        private readonly Queue<IMyCubeBlock> _mapConveyorQueue = new Queue<IMyCubeBlock>();

        /// <summary>
        /// Inventory that the Logistics Computer has already commited to a job or task.
        /// </summary>
        private readonly object _reservedInventoryLock = new object();
        /// <summary>
        /// Player set quotas for items.
        /// </summary>
        private readonly MyConcurrentDictionary<MyStringHash, QuotaSet> quotas = new MyConcurrentDictionary<MyStringHash, QuotaSet>(MyStringHash.Comparer);

        // Push configuration (Provider mode)
        private InventoryClass excessInventory; // Items to push when buffer is full
        private Dictionary<MyStringHash, int> bufferLimits; // Max amounts before pushing
        private bool autoPushEnabled = false;

        // Configuration
        private readonly int COMPONENT_CHECK_INTERVAL_TICKS = 600;
        private readonly int INVENTORY_SCAN_INTERVAL_TICKS = 180;
        private readonly int PUSH_CHECK_INTERVAL_TICKS = 300; // Check for excess every 5 seconds

        private int _messageCounter = 0;
        private int _bidRoundBidCounter = 0;
        private int _conveyorNetworkCounter = 0;
        private Orchestrator.Task _assignedSchedulerTask;
        private long _assignedSchedulerEntityId;
        private IMySessionDelegate sessionDelegate;

        public LogisticsComputer(
            IMyEntity entity,
            MessageQueue messaging,
            IAILogisticsComputerSettings settings,
            OperationMode operationMode = OperationMode.None,
            WorkMode workMode = WorkMode.None,
            IMySessionDelegate sessionDelegate = null)
        {
            this.Entity = entity;
            this.entityId = entity.EntityId;
            this.messaging = messaging;
            this.operationMode = operationMode;
            this.excessInventory = new InventoryClass();
            this.bufferLimits = new Dictionary<MyStringHash, int>(MyStringHash.Comparer);
            this.sessionDelegate = sessionDelegate ?? new MySessionDelegate();
            this._conveyorNetworkUpdateIntervalTicks = TimeUtil.TimeSpanToTick(TimeSpan.FromSeconds(settings.ConveyorNetworkUpdateIntervalSeconds));
            this._conveyorNetworkUpdateIntervalTicks = Math.Max(1, this._conveyorNetworkUpdateIntervalTicks);
        }

        public void Initialize()
        {
            currentState = State.Initializing;
            Log.Info("LogisticsComputer {0} initializing in mode: {1}", entityId, operationMode);

            ScanComponents();

            IMyCubeBlock cubeBlock = Entity as IMyCubeBlock;
            bool isStaticGrid = cubeBlock != null && cubeBlock.CubeGrid != null && cubeBlock.CubeGrid.IsStatic;
            messaging.RegisterAntenna(entityId, MessageQueue.IAIBlockType.LogisticsComputer, primaryAntenna, isStaticGrid);
            messaging.Subscribe(entityId, Channel.DRONE_TASK_ANNOUNCEMENT);

            // All logistics computers register themselves
            // The scheduler needs to know about all of them
            ScanCargoContainers();

            currentState = State.Active;
            isInitialized = true;
            Log.Info("LogisticsComputer {0} initialized successfully", entityId);
        }



        // public void Update()
        // {
        //     if (!isInitialized)
        //     {
        //         Initialize();
        //         return;
        //     }

        //     var currentFrame = sessionDelegate.GameplayFrameCounter;

        //     try
        //     {
        //         if (currentFrame - lastUpdateFrame >= COMPONENT_CHECK_INTERVAL_TICKS)
        //         {
        //             lastUpdateFrame = currentFrame;
        //             if (!CheckCapabilities())
        //             {
        //                 currentState = State.Error;
        //                 return;
        //             }
        //         }

        //         if (currentFrame - lastConnectorCheckFrame >= COMPONENT_CHECK_INTERVAL_TICKS)
        //         {
        //             lastConnectorCheckFrame = currentFrame;
        //             CheckConnectorChanges();
        //         }

        //         if (currentFrame - lastInventoryScanFrame >= INVENTORY_SCAN_INTERVAL_TICKS)
        //         {
        //             lastInventoryScanFrame = currentFrame;
        //             ScanCargoContainers(forceUpdate: false);
        //         }

        //         // Provider-specific: Check for excess inventory to push
        //         if (operationMode == OperationMode.PushOnly && autoPushEnabled)
        //         {
        //             if (currentFrame - lastPushCheckFrame >= PUSH_CHECK_INTERVAL_TICKS)
        //             {
        //                 lastPushCheckFrame = currentFrame;
        //                 CheckAndPushExcessInventory();
        //             }
        //         }

        //         // Requester-specific: Periodically check if needs are still unmet
        //         if (operationMode == OperationMode.RequestOnly)
        //         {
        //             // Requester sends LOGISTIC_REQUEST when it needs items
        //             // This is typically triggered by user action or automation logic
        //             // Not automatically handled in Update()
        //         }

        //         ProcessSchedulerBiddingAndAssignments();
        //     }
        //     catch (Exception ex)
        //     {
        //         Log.Error("LogisticsComputer {0} update error: {1}", entityId, ex.Message);
        //         currentState = State.Error;
        //     }
        // }

        public bool ScanComponents()
        {
            var cubeBlock = Entity as IMyCubeBlock;
            if (cubeBlock?.CubeGrid == null) return false;

            cargoContainers.Clear();
            connectors.Clear();
            collectors.Clear();
            assemblers.Clear();
            refineries.Clear();
            reactors.Clear();
            primaryAntenna = null;

            var connectedGrids = new List<IMyCubeGrid>();
            MyAPIGateway.GridGroups.GetGroup(cubeBlock.CubeGrid, GridLinkTypeEnum.Mechanical, connectedGrids);

            foreach (var grid in connectedGrids)
            {
                var blocks = new List<IMySlimBlock>();
                grid.GetBlocks(blocks);

                foreach (var block in blocks)
                {
                    var fatBlock = block.FatBlock;
                    if (fatBlock == null) continue;

                    if (fatBlock is IMyCargoContainer && fatBlock.IsFunctional)
                    {
                        cargoContainers.Add((IMyCargoContainer)fatBlock);
                    }

                    if (fatBlock is IMyShipConnector && fatBlock.IsFunctional)
                    {
                        connectors.Add((IMyShipConnector)fatBlock);
                    }
                    if (fatBlock is IMyCollector && fatBlock.IsFunctional)
                    {
                        collectors.Add((IMyCollector)fatBlock);
                    }
                    if (fatBlock is IMyAssembler && fatBlock.IsFunctional)
                    {
                        assemblers.Add((IMyAssembler)fatBlock);
                    }
                    if (fatBlock is IMyRefinery && fatBlock.IsFunctional)
                    {
                        refineries.Add((IMyRefinery)fatBlock);
                    }
                    if (fatBlock is IMyReactor && fatBlock.IsFunctional)
                    {
                        reactors.Add((IMyReactor)fatBlock);
                    }

                    if (fatBlock is IMyRadioAntenna)
                    {
                        var antenna = (IMyRadioAntenna)fatBlock;
                        if (antenna.IsFunctional && antenna.Enabled && antenna.EnableBroadcasting)
                        {
                            if (primaryAntenna == null || antenna.Radius > primaryAntenna.Radius)
                            {
                                primaryAntenna = antenna;
                            }
                        }
                    }
                }
            }

            return cargoContainers.Count > 0 && connectors.Count > 0 && primaryAntenna != null;
        }

        private void MapConveyorNetworks()
        {
            conveyorNetworks.Clear();
            conveyorNetworkLastUpdateFrame.Clear();
            var currentFrame = sessionDelegate.GameplayFrameCounter;
            if (currentFrame - _lastConveyorNetworkUpdateFrame < _conveyorNetworkUpdateIntervalTicks)
                return;
            _lastConveyorNetworkUpdateFrame = currentFrame;

            List<IMyCubeBlock> unvisited = new List<IMyCubeBlock>();
            foreach (IMyShipConnector b in connectors)
            {
                if (b != null) unvisited.Add(b);
            }
            foreach (IMyCollector b in collectors)
            {
                if (b != null) unvisited.Add(b);
            }
            foreach (IMyCargoContainer b in cargoContainers)
            {
                if (b != null) unvisited.Add(b);
            }
            foreach (IMyAssembler b in assemblers)
            {
                if (b != null) unvisited.Add(b);
            }
            foreach (IMyRefinery b in refineries)
            {
                if (b != null) unvisited.Add(b);
            }
            foreach (IMyReactor b in reactors)
            {
                if (b != null) unvisited.Add(b);
            }

            while (unvisited.Count > 0)
            {
                IMyCubeBlock seed = unvisited[0];
                unvisited.RemoveAt(0);

                ConveyorNetwork network = new ConveyorNetwork(sessionDelegate);
                network.ConveyorNetworkId = IdGenerator.GenerateId(ref _conveyorNetworkCounter, entityId);
                _mapConveyorQueue.Clear();
                _mapConveyorQueue.Enqueue(seed);

                while (_mapConveyorQueue.Count > 0)
                {
                    IMyCubeBlock current = _mapConveyorQueue.Dequeue();
                    ConveyorNetwork.CategorizeBlock(network, current);
                    IMyInventory currentInv = current.GetInventory(0);
                    if (currentInv == null) continue;

                    for (int j = unvisited.Count - 1; j >= 0; j--)
                    {
                        IMyCubeBlock potentialNeighbor = unvisited[j];
                        if (potentialNeighbor == null)
                        {
                            unvisited.RemoveAt(j);
                            continue;
                        }
                        IMyInventory neighborInv = potentialNeighbor.GetInventory(0);
                        if (neighborInv != null && currentInv.IsConnectedTo(neighborInv))
                        {
                            unvisited.RemoveAt(j);
                            _mapConveyorQueue.Enqueue(potentialNeighbor);
                        }
                    }
                }
                network.ConveyorNetworkId = IdGenerator.GenerateId(ref _conveyorNetworkCounter, entityId);
                network.Nickname = "Network " + network.ConveyorNetworkId;
                conveyorNetworks.Add(network);
            }
        }


        private void ScanCargoContainers()
        {
            foreach (var cn in conveyorNetworks)
            {
                if (cn.IsUpdating) continue;
                cn.UpdateInventories();
            }
        }


        // private bool HasInventoryChanged(Inventory oldInv, Inventory newInv)
        // {
        //     if (oldInv.GetItemTypeCount() != newInv.GetItemTypeCount()) return true;
        //     if (oldInv.GetTotalItemCount() != newInv.GetTotalItemCount()) return true;

        //     var oldItems = oldInv.GetAllItems();
        //     Dictionary<MyStringHash, int> oldLookup = new Dictionary<MyStringHash, int>(MyStringHash.Comparer);
        //     foreach (KVPair item in oldItems)
        //     {
        //         oldLookup[item.Key] = item.Value;
        //     }

        //     List<KVPair> newItems = newInv.GetAllItems();
        //     foreach (KVPair newItem in newItems)
        //     {
        //         int oldCount;
        //         if (!oldLookup.TryGetValue(newItem.Key, out oldCount) || oldCount != newItem.Value)
        //         {
        //             return true;
        //         }
        //     }

        //     return false;
        // }


        private void UpdateConnectorPositions()
        {
            foreach (var cn in conveyorNetworks)
            {
                if (cn.IsUpdating) continue;
                cn.UpdateIOLocationData();
            }
        }


        private void ReadAuctionMessages()
        {
            messaging.ReadMessages(
                entityId,
                primaryAntenna,
                Channel.ORCHESTRATOR_AUCTION_START,
                auctionInbox,
                maxMessages: 10,
                clear: true,
                messageFilters: PayloadType.Auction);
            foreach (var message in auctionInbox)
            {
                Log.Info("LogisticsComputer {0} received bid round start for job {1} of type {2}", entityId, message.Payload.JobId, message.Payload.JobType);
                Auction auction = message.Payload;
                if (auction == null)
                    continue;
                if (auction.ExpirationTime < DateTime.UtcNow)
                    continue;
                if (auction.ComponentsInventory.IsEmpty())
                    continue;

                bidOutbox.Clear();
                switch (auction.JobType)
                {
                    case Orchestrator.JobType.WeldBlock:
                        uint bidId = IdGenerator.GenerateId(ref _bidRoundBidCounter, entityId);
                        // check each conveyor network, if one can fully fulfill the request,
                        // send bid immediately
                        // if only partial fulfills are available, store them in the cache
                        // conveyor networks already expected to be sorted by priority
                        foreach (var cn in conveyorNetworks)
                        {
                            if (cn.IsUpdating) continue;

                            var fulfillmentData = cn.CanFulfillRequest(auction.ComponentsInventory, auction.AuctionId);
                            if (fulfillmentData.Fulfillment.HasFlag(InventoryFulfillment.SatisfyFully) ||
                                fulfillmentData.Fulfillment.HasFlag(InventoryFulfillment.SatisfyPartial))
                            {
                                // enqueue partial or total fulfillment
                                bidOutbox.Enqueue(new Message<Bid>
                                {
                                    Payload = new Bid
                                    {
                                        EntityId = entityId,
                                        EntityType = IAIEntityType.LogisticsComputerBlock,
                                        JobId = auction.JobId,
                                        AuctionId = auction.AuctionId,
                                        BidId = bidId,
                                        InventoryFulfillmentFlags = fulfillmentData.Fulfillment,
                                        BidInventory = fulfillmentData.Inventory,
                                        IOLocationData = fulfillmentData.IOBlockPositions[0],
                                    },
                                    MessageId = IdGenerator.GenerateId(ref _messageCounter, entityId),
                                    CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                                    SenderId = entityId,
                                    SenderOwnerId = (Entity as IMyCubeBlock) != null ? ((IMyCubeBlock)Entity).OwnerId : 0L,
                                    RequiresAck = false,
                                    Channel = Channel.ORCHESTRATOR_AUCTION_BIDS,
                                });
                                Log.Info("LogisticsComputer {0} enqueued auction bid {1} for job {2} of type {3}", entityId, bidId, auction.JobId, auction.JobType);
                            }
                        }
                        break;

                    // case Orchestrator.JobType.WeldBlock:
                    //     availableInventory = currentInventory.Substract(reservedInventory);
                    //     if (availableInventory.ContainsAtLeast(bidRoundStart.ComponentsInventory))
                    //     inventoryFulfillment |= InventoryFulfillment.SatisfyFully;
                    //     break;
                    // case Orchestrator.JobType.Logistics:
                    //     inventoryFulfillment = InventoryFulfillment.SatisfyFully;
                    //     break;
                    default:
                        break;
                }
            }
        }

        public void SendBidRoundBids()
        {
            while (bidOutbox.Count > 0)
            {
                int messagesSent = 0;
                Message<Bid> bidMessage;
                if (bidOutbox.TryDequeue(out bidMessage))
                {
                    messaging.BroadcastMessage(primaryAntenna, bidMessage);
                    messagesSent++;
                }
                if (messagesSent > 0)
                {
                    Log.Info("LogisticsComputer {0} sent {1} bid round bids", entityId, messagesSent);
                }
            }
        }



        public void EnableAutoPush(bool enabled)
        {
            autoPushEnabled = enabled;
            Log.Info("LogisticsComputer {0} auto-push {1}", entityId, enabled ? "enabled" : "disabled");
        }

        public bool IsOperational()
        {
            return isInitialized && currentState == State.Active &&
                   cargoContainers.Count > 0 && connectors.Count > 0 &&
                   primaryAntenna != null && primaryAntenna.IsWorking;
        }

        public OperationMode GetOperationMode()
        {
            return operationMode;
        }


        public void SetConveyorNetworkPriority(ConveyorNetwork network, int priority)
        {
            network.Priority = priority;
            SyncConveyorNetworksOrder();
        }
        public void SyncConveyorNetworksOrder()
        {
            conveyorNetworks.Sort();
        }
    }
}
