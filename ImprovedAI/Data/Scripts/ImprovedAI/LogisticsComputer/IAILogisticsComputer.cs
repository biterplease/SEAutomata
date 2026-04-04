using ImprovedAI.VirtualNetwork;
using ImprovedAI.Util;
using ImprovedAI.Util.Logging;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

using static ImprovedAI.LogisticsComputer;

namespace ImprovedAI
{
    public class IAILogisticsComputer
    {
        private readonly long entityId;
        private readonly IMyEntity Entity;
        private readonly MessageQueue messaging;

        // Components
        private List<IMyCargoContainer> cargoContainers = new List<IMyCargoContainer>();
        private List<IMyShipConnector> connectors = new List<IMyShipConnector>();
        private IMyRadioAntenna primaryAntenna;

        // State
        private State currentState = State.Initializing;
        private OperationMode operationMode;
        private bool isInitialized = false;
        private long lastUpdateFrame = 0;
        private long lastInventoryScanFrame = 0;
        private long lastConnectorCheckFrame = 0;
        private long lastPushCheckFrame = 0;

        // Cached data
        private Inventory cachedInventory;
        private List<Vector3D> cachedConnectorPositions = new List<Vector3D>();
        private int lastConnectorCount = 0;

        // Push configuration (Provider mode)
        private Inventory excessInventory; // Items to push when buffer is full
        private Dictionary<string, int> bufferLimits; // Max amounts before pushing
        private bool autoPushEnabled = false;

        // Configuration
        private readonly int COMPONENT_CHECK_INTERVAL_TICKS = 600;
        private readonly int INVENTORY_SCAN_INTERVAL_TICKS = 180;
        private readonly int PUSH_CHECK_INTERVAL_TICKS = 300; // Check for excess every 5 seconds

        private int _lcMessageCounter = 0;
        private readonly List<Message<TaskAnnouncement>> _schedulerAnnouncementCache = new List<Message<TaskAnnouncement>>();
        private readonly List<Message<TaskAssignment>> _schedulerAssignmentCache = new List<Message<TaskAssignment>>();
        private Scheduler.Task _assignedSchedulerTask;
        private long _assignedSchedulerEntityId;

        public IAILogisticsComputer(IMyEntity entity, MessageQueue messaging,OperationMode operationMode = OperationMode.ProvideForConstruction)
        {
            this.Entity = entity;
            this.entityId = entity.EntityId;
            this.messaging = messaging;
            this.operationMode = operationMode;
            this.cachedInventory = new Inventory();
            this.excessInventory = new Inventory();
            this.bufferLimits = new Dictionary<string, int>();
        }

        public void Initialize()
        {
            currentState = State.Initializing;
            Log.Info("LogisticsComputer {0} initializing in mode: {1}", entityId, operationMode);

            if (!CheckCapabilities())
            {
                Log.Error("LogisticsComputer {0} failed capability check", entityId);
                currentState = State.Error;
                return;
            }

            IMyCubeBlock cubeBlock = Entity as IMyCubeBlock;
            bool isStaticGrid = cubeBlock != null && cubeBlock.CubeGrid != null && cubeBlock.CubeGrid.IsStatic;
            messaging.RegisterAntenna(entityId, MessageQueue.IAIBlockType.LogisticsComputer, primaryAntenna, isStaticGrid);
            messaging.Subscribe(entityId, Channel.DRONE_TASK_ANNOUNCEMENT);

            // All logistics computers register themselves
            // The scheduler needs to know about all of them
            ScanCargoContainers(forceUpdate: true);
            SendRegistrationMessage();

            currentState = State.Active;
            isInitialized = true;
            Log.Info("LogisticsComputer {0} initialized successfully", entityId);
        }

        public void Update()
        {
            if (!isInitialized)
            {
                Initialize();
                return;
            }

            var currentFrame = MyAPIGateway.Session.GameplayFrameCounter;

            try
            {
                if (currentFrame - lastUpdateFrame >= COMPONENT_CHECK_INTERVAL_TICKS)
                {
                    lastUpdateFrame = currentFrame;
                    if (!CheckCapabilities())
                    {
                        currentState = State.Error;
                        return;
                    }
                }

                if (currentFrame - lastConnectorCheckFrame >= COMPONENT_CHECK_INTERVAL_TICKS)
                {
                    lastConnectorCheckFrame = currentFrame;
                    CheckConnectorChanges();
                }

                if (currentFrame - lastInventoryScanFrame >= INVENTORY_SCAN_INTERVAL_TICKS)
                {
                    lastInventoryScanFrame = currentFrame;
                    ScanCargoContainers(forceUpdate: false);
                }

                // Provider-specific: Check for excess inventory to push
                if (operationMode == OperationMode.Push && autoPushEnabled)
                {
                    if (currentFrame - lastPushCheckFrame >= PUSH_CHECK_INTERVAL_TICKS)
                    {
                        lastPushCheckFrame = currentFrame;
                        CheckAndPushExcessInventory();
                    }
                }

                // Requester-specific: Periodically check if needs are still unmet
                if (operationMode == OperationMode.Request)
                {
                    // Requester sends LOGISTIC_REQUEST when it needs items
                    // This is typically triggered by user action or automation logic
                    // Not automatically handled in Update()
                }

                ProcessSchedulerBiddingAndAssignments();
            }
            catch (Exception ex)
            {
                Log.Error("LogisticsComputer {0} update error: {1}", entityId, ex.Message);
                currentState = State.Error;
            }
        }

        private bool CheckCapabilities()
        {
            var cubeBlock = Entity as IMyCubeBlock;
            if (cubeBlock?.CubeGrid == null) return false;

            cargoContainers.Clear();
            connectors.Clear();
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

        private void ScanCargoContainers(bool forceUpdate)
        {
            var newInventory = new Inventory();
            foreach (var container in cargoContainers)
            {
                if (!container.IsFunctional || !container.HasInventory) continue;
                var inventory = container.GetInventory();
                if (inventory == null) continue;

                var items = new List<VRage.Game.ModAPI.Ingame.MyInventoryItem>();
                inventory.GetItems(items);

                foreach (var item in items)
                {
                    var subtypeId = item.Type.SubtypeId.ToString();
                    var amount = (int)item.Amount;
                    if (amount > 0)
                    {
                        newInventory.AddItem(subtypeId, amount);
                    }
                }
            }

            if (forceUpdate || HasInventoryChanged(cachedInventory, newInventory))
            {
                cachedInventory = newInventory;
                SendUpdateMessage();
            }

            CheckAssignedLogisticsFulfillment();
        }

        private bool HasInventoryChanged(Inventory oldInv, Inventory newInv)
        {
            if (oldInv.GetItemTypeCount() != newInv.GetItemTypeCount()) return true;
            if (oldInv.GetTotalItemCount() != newInv.GetTotalItemCount()) return true;

            var oldItems = oldInv.GetAllItems();
            var oldLookup = new Dictionary<string, int>();
            foreach (var item in oldItems)
            {
                oldLookup[item.Key] = item.Value;
            }

            var newItems = newInv.GetAllItems();
            foreach (var newItem in newItems)
            {
                int oldCount;
                if (!oldLookup.TryGetValue(newItem.Key, out oldCount) || oldCount != newItem.Value)
                {
                    return true;
                }
            }

            return false;
        }

        private void CheckConnectorChanges()
        {
            if (connectors.Count != lastConnectorCount)
            {
                lastConnectorCount = connectors.Count;
                UpdateConnectorPositions();
                SendUpdateMessage();
            }
        }

        private void UpdateConnectorPositions()
        {
            cachedConnectorPositions.Clear();
            foreach (var connector in connectors)
            {
                if (connector.IsFunctional)
                {
                    cachedConnectorPositions.Add(connector.GetPosition());
                }
            }
        }

        private void SendRegistrationMessage()
        {
            UpdateConnectorPositions();

            var registration = new LogisticsUpdate
            {
                EntityId = entityId,
                Inventory = cachedInventory,
                Connectors = cachedConnectorPositions,
                Timestamp = DateTime.UtcNow,
                OperationMode = operationMode
            };

            messaging.SendMessage((ushort)Channel.LOGISTIC_REGISTRATION, registration, entityId, requiresAck: false);
            Log.Info("LogisticsComputer {0} sent registration as {1}", entityId, operationMode);
        }

        private void SendUpdateMessage()
        {
            UpdateConnectorPositions();

            var update = new LogisticsUpdate
            {
                EntityId = entityId,
                Inventory = cachedInventory,
                Connectors = cachedConnectorPositions,
                Timestamp = DateTime.UtcNow,
                OperationMode = operationMode
            };

            messaging.SendMessage((ushort)Channel.LOGISTIC_UPDATE, update, entityId, requiresAck: false);
        }

        /// <summary>
        /// Check if any items exceed buffer limits and push them to the network
        /// </summary>
        private void CheckAndPushExcessInventory()
        {
            if (bufferLimits.Count == 0) return;

            var itemsToPush = new Inventory();
            var allItems = cachedInventory.GetAllItems();

            foreach (var item in allItems)
            {
                int bufferLimit;
                if (bufferLimits.TryGetValue(item.Key, out bufferLimit))
                {
                    if (item.Value > bufferLimit)
                    {
                        int excessAmount = item.Value - bufferLimit;
                        itemsToPush.AddItem(item.Key, excessAmount);
                    }
                }
            }

            if (!itemsToPush.IsEmpty())
            {
                SendPushMessage(itemsToPush);
                Log.Info("LogisticsComputer {0} pushing {1} excess item types to network",
                    entityId, itemsToPush.GetItemTypeCount());
            }
        }

        /// <summary>
        /// Send a LOGISTIC_PUSH message to get rid of unwanted/excess inventory
        /// Scheduler will dispatch a drone to pick it up
        /// </summary>
        private void SendPushMessage(Inventory inventory)
        {
            // TODO: connector may not be attached to cargo containers, need to fix
            var connector = connectors[0];
            var pushRequest = new InventoryRequisition
            {
                RequestingEntityId = entityId,
                Inventory = inventory,
                ConnectorLocation = connector.GetPosition(),
                Timestamp = DateTime.UtcNow,
                RequisitionType = Inventory.RequisitionType.Pull,
                IsStatic = connector.CubeGrid.IsStatic
            }; 

            messaging.SendMessage((ushort)Channel.LOGISTIC_PUSH, pushRequest, entityId, requiresAck: false);
        }

        /// <summary>
        /// Request specific items from the network at specific connector.
        /// Scheduler will find a Provider and dispatch a drone to deliver
        /// </summary>
        public void RequestInventory(IMyShipConnector connector, Inventory requestedInventory)
        {
            if (operationMode != OperationMode.Request)
            {
                Log.Warning("LogisticsComputer {0} cannot request inventory - not in Requester mode", entityId);
                return;
            }

            if (requestedInventory == null || requestedInventory.IsEmpty())
            {
                Log.Warning("LogisticsComputer {0} cannot request empty inventory", entityId);
                return;
            }

            var request = new InventoryRequisition
            {
                RequestingEntityId = entityId,
                Inventory = requestedInventory,
                ConnectorLocation = connector.GetPosition(),
                Timestamp = DateTime.UtcNow,
                RequisitionType = Inventory.RequisitionType.Pull,
                IsStatic = connector.CubeGrid.IsStatic
            };

            messaging.SendMessage((ushort)Channel.LOGISTIC_REQUEST, request, entityId, requiresAck: false);
            Log.Info("LogisticsComputer {0} requested {1} item types from network",
                entityId, requestedInventory.GetItemTypeCount());
        }

        /// <summary>
        /// Manually push specific inventory (even if not excess)
        /// </summary>
        public void PushInventory(IMyShipConnector connector ,Inventory inventoryToPush)
        {
            if (operationMode != OperationMode.Push)
            {
                Log.Warning("LogisticsComputer {0} cannot push inventory - not in Provider mode", entityId);
                return;
            }

            if (inventoryToPush == null || inventoryToPush.IsEmpty())
            {
                Log.Warning("LogisticsComputer {0} cannot push empty inventory", entityId);
                return;
            }

            // Verify we actually have these items
            var allItems = inventoryToPush.GetAllItems();
            foreach (var item in allItems)
            {
                if (cachedInventory.GetItemCount(item.Key) < item.Value)
                {
                    Log.Warning("LogisticsComputer {0} cannot push {1}x {2} - only have {3}",
                        entityId, item.Value, item.Key, cachedInventory.GetItemCount(item.Key));
                    return;
                }
            }

            SendPushMessage(inventoryToPush);
        }

        /// <summary>
        /// Set buffer limits for auto-push functionality
        /// When inventory exceeds these limits, excess is automatically pushed
        /// </summary>
        public void SetBufferLimit(string itemSubtypeId, int maxAmount)
        {
            if (operationMode != OperationMode.Push)
            {
                Log.Warning("LogisticsComputer {0} cannot set buffer limits - not in Provider mode", entityId);
                return;
            }

            bufferLimits[itemSubtypeId] = maxAmount;
            Log.Info("LogisticsComputer {0} set buffer limit for {1}: {2}", entityId, itemSubtypeId, maxAmount);
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

        public Inventory GetCurrentInventory()
        {
            return cachedInventory;
        }

        public List<Vector3D> GetConnectorPositions()
        {
            return new List<Vector3D>(cachedConnectorPositions);
        }

        private void ProcessSchedulerBiddingAndAssignments()
        {
            if (primaryAntenna == null || currentState != State.Active)
                return;

            messaging.ReadMessages(
                entityId,
                primaryAntenna,
                Channel.DRONE_TASK_ANNOUNCEMENT,
                _schedulerAnnouncementCache,
                maxMessages: 10,
                clear: true,
                messageFilters: PayloadType.TaskAnnouncement);

            for (int i = 0; i < _schedulerAnnouncementCache.Count; i++)
            {
                TaskAnnouncement ann = _schedulerAnnouncementCache[i].Payload;
                if (ann == null)
                    continue;
                if (!SchedulerTaskCapabilities.IsLogisticsTaskType(ann.Type))
                    continue;

                Inventory req = ann.RequiredPayload;
                if (req != null && !req.IsEmpty() && !cachedInventory.ContainsAtLeast(req))
                    continue;

                double minDistSq = double.MaxValue;
                for (int c = 0; c < cachedConnectorPositions.Count; c++)
                {
                    double d2 = Vector3D.DistanceSquared(cachedConnectorPositions[c], ann.Destination);
                    if (d2 < minDistSq)
                        minDistSq = d2;
                }
                if (minDistSq >= double.MaxValue * 0.5 && cachedConnectorPositions.Count == 0)
                    continue;

                float dist = minDistSq < double.MaxValue * 0.5 ? (float)Math.Sqrt(minDistSq) : 100f;
                const float assumedSpeed = 10f;
                float estimatedTime = dist / assumedSpeed;
                float pathComplexity = (float)Math.Min(1d, minDistSq / 2000000.0);
                float cargoAvailability = 0.5f;

                TaskBid bid = new TaskBid
                {
                    TaskId = ann.TaskId,
                    EstimatedTime = estimatedTime,
                    PathComplexity = pathComplexity,
                    BidderKind = TaskBidderKind.LogisticsComputer,
                    CargoAvailability = cargoAvailability,
                };

                Message<TaskBid> bidMsg = new Message<TaskBid>
                {
                    Payload = bid,
                    MessageId = IdGenerator.GenerateId(ref _lcMessageCounter, entityId),
                    CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                    RecipientId = ann.SchedulerEntityId,
                    SenderId = entityId,
                    SenderOwnerId = (Entity as IMyCubeBlock) != null ? ((IMyCubeBlock)Entity).OwnerId : 0L,
                    RequiresAck = false,
                    RecipientBlockType = MessageQueue.IAIBlockType.Scheduler,
                    Channel = Channel.DIRECT_MESSAGE,
                };

                messaging.SendDirectMessage(primaryAntenna, bidMsg, false);
                Log.Verbose("LogisticsComputer {0} sent TaskBid for task {1}", entityId, ann.TaskId);
            }

            messaging.ReadMessages(
                entityId,
                primaryAntenna,
                Channel.DIRECT_MESSAGE,
                _schedulerAssignmentCache,
                maxMessages: 10,
                clear: true,
                messageFilters: PayloadType.TaskAssignment);

            for (int a = 0; a < _schedulerAssignmentCache.Count; a++)
            {
                Message<TaskAssignment> msg = _schedulerAssignmentCache[a];
                if (msg.RecipientId != entityId)
                    continue;
                TaskAssignment assignment = msg.Payload;
                if (assignment == null || assignment.Tasks == null || assignment.Tasks.Count == 0)
                    continue;

                _assignedSchedulerTask = assignment.Tasks[0];
                _assignedSchedulerEntityId = msg.SenderId;
                Log.Info("LogisticsComputer {0} received scheduler task assignment {1}", entityId, _assignedSchedulerTask.TaskId);
            }
        }

        private void CheckAssignedLogisticsFulfillment()
        {
            if (_assignedSchedulerTask == null || primaryAntenna == null || _assignedSchedulerEntityId == 0L)
                return;

            Inventory pay = _assignedSchedulerTask.Payload;
            if (pay == null || pay.IsEmpty())
                return;

            if (!cachedInventory.ContainsAtLeast(pay))
                SendTaskFulfillmentLost("Inventory no longer satisfies assigned task payload");
        }

        private void SendTaskFulfillmentLost(string reason)
        {
            if (_assignedSchedulerTask == null || _assignedSchedulerEntityId == 0L || primaryAntenna == null)
                return;

            Message<TaskFulfillmentLost> msg = new Message<TaskFulfillmentLost>
            {
                Payload = new TaskFulfillmentLost
                {
                    TaskId = _assignedSchedulerTask.TaskId,
                    Reason = reason,
                },
                MessageId = IdGenerator.GenerateId(ref _lcMessageCounter, entityId),
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                RecipientId = _assignedSchedulerEntityId,
                SenderId = entityId,
                SenderOwnerId = (Entity as IMyCubeBlock) != null ? ((IMyCubeBlock)Entity).OwnerId : 0L,
                RequiresAck = false,
                RecipientBlockType = MessageQueue.IAIBlockType.Scheduler,
                Channel = Channel.DIRECT_MESSAGE,
            };

            messaging.SendDirectMessage(primaryAntenna, msg, false);
            uint lostTaskId = _assignedSchedulerTask.TaskId;
            Log.Warning("LogisticsComputer {0} notified scheduler of fulfillment loss for task {1}", entityId, lostTaskId);
            _assignedSchedulerTask = null;
            _assignedSchedulerEntityId = 0L;
        }
    }
}
