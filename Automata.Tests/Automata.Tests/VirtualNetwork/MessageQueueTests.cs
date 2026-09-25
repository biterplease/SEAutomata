using ImprovedAI.Config;
using ImprovedAI.Tests.TestUtil;
using ImprovedAI.Util;
using ImprovedAI.VirtualNetwork;
using Mq = ImprovedAI.VirtualNetwork.MessageQueue;
using static ImprovedAI.Scheduler;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using VRage.Game;
using VRageMath;

namespace ImprovedAI.Tests.VirtualNetwork
{
    /// <summary>
    /// Ack behaviour: production code clears pending ack on the <b>sender</b> when the recipient successfully
    /// decodes a message with <see cref="Message{T}.RequiresAck"/> (see MessageQueue.ReadMessages). Drone/LC
    /// outbound messages today set RequiresAck = false.
    /// </summary>
    [TestClass]
    public class VirtualNetworkMessageQueueTests
    {
        [TestInitialize]
        public void Setup()
        {
            TryResetMessageQueue();
            FakeMessageQueueConfig config = new FakeMessageQueueConfig();
            config.messageRetentionTicks = 60000;
            config.dlqMessageRetentionTicks = 120000;
            Mq.Init(config, new ProtoBufMyUtilitiesDelegate(), new InterlockedDelegate());
        }

        [TestCleanup]
        public void Teardown()
        {
            TryResetMessageQueue();
        }

        private static void TryResetMessageQueue()
        {
            try
            {
                Mq.Instance.Reset();
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static Mock<IMyRadioAntenna> CreateAntenna(long ownerId, Vector3D position, float radius)
        {
            Mock<IMyRadioAntenna> antenna = new Mock<IMyRadioAntenna>();
            antenna.Setup(a => a.GetPosition()).Returns(position);
            antenna.Setup(a => a.Radius).Returns(radius);
            antenna.Setup(a => a.OwnerId).Returns(ownerId);
            antenna.Setup(a => a.Enabled).Returns(true);
            antenna.Setup(a => a.EnableBroadcasting).Returns(true);
            antenna.Setup(a => a.IsWorking).Returns(true);
            antenna.Setup(a => a.IsFunctional).Returns(true);
            antenna.Setup(a => a.Closed).Returns(false);
            antenna.Setup(a => a.MarkedForClose).Returns(false);
            return antenna;
        }

        [TestMethod]
        public void Subscribe_And_Broadcast_LogisticsUpdate_Recipient_Reads_And_Decodes()
        {
            Mq mq = Mq.Instance;
            const long schedulerId = 101L;
            const long lcId = 202L;
            Mock<IMyRadioAntenna> antSched = CreateAntenna(1L, new Vector3D(100, 0, 0), 8000f);
            Mock<IMyRadioAntenna> antLc = CreateAntenna(1L, new Vector3D(200, 0, 0), 8000f);

            mq.RegisterAntenna(schedulerId, Mq.IAIBlockType.Scheduler, antSched.Object);
            mq.RegisterAntenna(lcId, Mq.IAIBlockType.LogisticsComputer, antLc.Object);
            mq.Subscribe(lcId, Channel.LOGISTIC_REGISTRATION);

            LogisticsUpdate payload = new LogisticsUpdate
            {
                EntityId = lcId,
                Inventory = new Inventory(),
                Connectors = new List<Vector3D>(),
                OperationMode = LogisticsComputer.OperationMode.ProvideForConstruction,
                Timestamp = DateTime.UtcNow
            };
            Message<LogisticsUpdate> message = new Message<LogisticsUpdate>
            {
                MessageId = 1,
                Payload = payload,
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = schedulerId,
                SenderOwnerId = 1L,
                RequiresAck = false,
                SerializationMode = MessageSerializationMode.ProtoBuf,
                RecipientBlockType = Mq.IAIBlockType.LogisticsComputer,
                Channel = Channel.LOGISTIC_REGISTRATION
            };

            ErrorCode sendResult = mq.BroadcastMessage(antSched.Object, message, enforceCommsRange: false);
            Assert.AreEqual(ErrorCode.None, sendResult);

            List<Message<LogisticsUpdate>> received = new List<Message<LogisticsUpdate>>();
            ErrorCode readResult = mq.ReadMessages(lcId, antLc.Object, Channel.LOGISTIC_REGISTRATION, received, 10, true, PayloadType.LogisticsUpdate);
            Assert.AreEqual(ErrorCode.None, readResult);
            Assert.AreEqual(1, received.Count);
            Assert.AreEqual(lcId, received[0].Payload.EntityId);
        }

        [TestMethod]
        public void SendDirectMessage_DroneReport_To_Scheduler_Delivers()
        {
            Mq mq = Mq.Instance;
            const long droneId = 301L;
            const long schedId = 302L;
            Mock<IMyRadioAntenna> antDrone = CreateAntenna(5L, new Vector3D(0, 0, 0), 5000f);
            Mock<IMyRadioAntenna> antSched = CreateAntenna(5L, new Vector3D(50, 0, 0), 5000f);

            mq.RegisterAntenna(droneId, Mq.IAIBlockType.Drone, antDrone.Object);
            mq.RegisterAntenna(schedId, Mq.IAIBlockType.Scheduler, antSched.Object);
            mq.Subscribe(schedId, Channel.DIRECT_MESSAGE);

            DroneReport report = new DroneReport
            {
                DroneEntityId = droneId,
                Flags = Drone.UpdateFlags.Registration
            };
            Message<DroneReport> msg = new Message<DroneReport>
            {
                MessageId = 7,
                Payload = report,
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = droneId,
                SenderOwnerId = 5L,
                RecipientId = schedId,
                RequiresAck = false,
                SerializationMode = MessageSerializationMode.ProtoBuf,
                RecipientBlockType = Mq.IAIBlockType.Scheduler,
                Channel = Channel.DIRECT_MESSAGE
            };

            ErrorCode err = mq.SendDirectMessage(antDrone.Object, msg, enforceCommsRange: false);
            Assert.AreEqual(ErrorCode.None, err);

            List<Message<DroneReport>> inbox = new List<Message<DroneReport>>();
            ErrorCode read = mq.ReadMessages(schedId, antSched.Object, Channel.DIRECT_MESSAGE, inbox, 10, true, PayloadType.DroneReport);
            Assert.AreEqual(ErrorCode.None, read);
            Assert.AreEqual(1, inbox.Count);
            DroneReport decoded = inbox[0].Payload;
            Assert.IsNotNull(decoded);
            Assert.AreEqual(droneId, decoded.DroneEntityId);
        }

        [TestMethod]
        public void SendDirectMessage_OutOfRange_Returns_RecipientNotInRange()
        {
            Mq mq = Mq.Instance;
            const long droneId = 401L;
            const long schedId = 402L;
            const long sharedOwner = 44L;
            Mock<IMyRadioAntenna> antDrone = CreateAntenna(sharedOwner, new Vector3D(0, 0, 0), 100f);
            Mock<IMyRadioAntenna> antSched = CreateAntenna(sharedOwner, new Vector3D(150, 0, 0), 8000f);

            mq.RegisterAntenna(droneId, Mq.IAIBlockType.Drone, antDrone.Object);
            mq.RegisterAntenna(schedId, Mq.IAIBlockType.Scheduler, antSched.Object);
            mq.Subscribe(schedId, Channel.DIRECT_MESSAGE);

            Message<DroneReport> msg = new Message<DroneReport>
            {
                MessageId = 1,
                Payload = new DroneReport { DroneEntityId = droneId },
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = droneId,
                SenderOwnerId = sharedOwner,
                RecipientId = schedId,
                RequiresAck = false,
                SerializationMode = MessageSerializationMode.ProtoBuf,
                RecipientBlockType = Mq.IAIBlockType.Scheduler,
                Channel = Channel.DIRECT_MESSAGE
            };

            ErrorCode err = mq.SendDirectMessage(antDrone.Object, msg, enforceCommsRange: false);
            Assert.AreEqual(ErrorCode.RecipientNotInRange, err);
        }

        [TestMethod]
        public void PayloadType_Filter_ReEnqueues_NonMatching_Then_Reads_With_Correct_Filter()
        {
            Mq mq = Mq.Instance;
            const long senderId = 501L;
            const long receiverId = 502L;
            Mock<IMyRadioAntenna> antSend = CreateAntenna(1L, new Vector3D(0, 0, 0), 9000f);
            Mock<IMyRadioAntenna> antRecv = CreateAntenna(1L, new Vector3D(100, 0, 0), 9000f);
            mq.RegisterAntenna(senderId, Mq.IAIBlockType.Scheduler, antSend.Object);
            mq.RegisterAntenna(receiverId, Mq.IAIBlockType.Scheduler, antRecv.Object);
            mq.Subscribe(receiverId, Channel.LOGISTIC_UPDATE);

            TaskAssignment taskAssignment = new TaskAssignment { Tasks = new List<Task>() };
            Message<TaskAssignment> broadcast = new Message<TaskAssignment>
            {
                MessageId = 10,
                Payload = taskAssignment,
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = senderId,
                SenderOwnerId = 1L,
                RequiresAck = false,
                SerializationMode = MessageSerializationMode.ProtoBuf,
                RecipientBlockType = Mq.IAIBlockType.None,
                Channel = Channel.LOGISTIC_UPDATE
            };
            Assert.AreEqual(ErrorCode.None, mq.BroadcastMessage(antSend.Object, broadcast, false));

            List<Message<LogisticsUpdate>> wrongFilter = new List<Message<LogisticsUpdate>>();
            mq.ReadMessages(receiverId, antRecv.Object, Channel.LOGISTIC_UPDATE, wrongFilter, 10, true, PayloadType.LogisticsUpdate);
            Assert.AreEqual(0, wrongFilter.Count);

            List<Message<TaskAssignment>> rightFilter = new List<Message<TaskAssignment>>();
            mq.ReadMessages(receiverId, antRecv.Object, Channel.LOGISTIC_UPDATE, rightFilter, 10, true, PayloadType.TaskAssignment);
            Assert.AreEqual(1, rightFilter.Count);
        }

        [TestMethod]
        public void RequiresAck_ReadByRecipient_Clears_Pending_For_Sender()
        {
            Mq mq = Mq.Instance;
            const long senderId = 601L;
            const long recipientId = 602L;
            Mock<IMyRadioAntenna> antSend = CreateAntenna(1L, new Vector3D(0, 0, 0), 6000f);
            Mock<IMyRadioAntenna> antRecv = CreateAntenna(1L, new Vector3D(100, 0, 0), 6000f);
            mq.RegisterAntenna(senderId, Mq.IAIBlockType.Scheduler, antSend.Object);
            mq.RegisterAntenna(recipientId, Mq.IAIBlockType.Drone, antRecv.Object);
            mq.Subscribe(recipientId, Channel.DRONE_TASK_ASSIGNMENT);

            LogisticsUpdate lu = new LogisticsUpdate { EntityId = recipientId, Inventory = new Inventory() };
            Message<LogisticsUpdate> msg = new Message<LogisticsUpdate>
            {
                MessageId = 42,
                Payload = lu,
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = senderId,
                SenderOwnerId = 1L,
                RequiresAck = true,
                SerializationMode = MessageSerializationMode.ProtoBuf,
                RecipientBlockType = Mq.IAIBlockType.Drone,
                Channel = Channel.DRONE_TASK_ASSIGNMENT
            };

            Assert.AreEqual(ErrorCode.None, mq.BroadcastMessage(antSend.Object, msg, false));
            Assert.IsTrue(GetPendingAckCount(mq, senderId) > 0);

            List<Message<LogisticsUpdate>> got = new List<Message<LogisticsUpdate>>();
            Assert.AreEqual(ErrorCode.None, mq.ReadMessages(recipientId, antRecv.Object, Channel.DRONE_TASK_ASSIGNMENT, got, 10, true, PayloadType.LogisticsUpdate));
            Assert.AreEqual(1, got.Count);
            Assert.AreEqual(0, GetPendingAckCount(mq, senderId));
        }

        [TestMethod]
#pragma warning disable 618 // Thread.Sleep in test
        public void Expired_RequiresAck_Goes_To_DeadLetter_Queue()
        {
            TryResetMessageQueue();
            FakeMessageQueueConfig shortCfg = new FakeMessageQueueConfig();
            shortCfg.messageRetentionTicks = 1;
            shortCfg.dlqMessageRetentionTicks = 60000;
            Mq.Init(shortCfg, new ProtoBufMyUtilitiesDelegate(), new InterlockedDelegate());
            Mq mq = Mq.Instance;

            const long senderId = 701L;
            const long recipientId = 702L;
            Mock<IMyRadioAntenna> antSend = CreateAntenna(1L, new Vector3D(0, 0, 0), 6000f);
            Mock<IMyRadioAntenna> antRecv = CreateAntenna(1L, new Vector3D(50, 0, 0), 6000f);
            mq.RegisterAntenna(senderId, Mq.IAIBlockType.Scheduler, antSend.Object);
            mq.RegisterAntenna(recipientId, Mq.IAIBlockType.Drone, antRecv.Object);
            mq.Subscribe(recipientId, Channel.DRONE_TASK_ASSIGNMENT);

            Message<LogisticsUpdate> msg = new Message<LogisticsUpdate>
            {
                MessageId = 99,
                Payload = new LogisticsUpdate { EntityId = recipientId, Inventory = new Inventory() },
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = senderId,
                SenderOwnerId = 1L,
                RequiresAck = true,
                SerializationMode = MessageSerializationMode.ProtoBuf,
                RecipientBlockType = Mq.IAIBlockType.Drone,
                Channel = Channel.DRONE_TASK_ASSIGNMENT
            };
            Assert.AreEqual(ErrorCode.None, mq.BroadcastMessage(antSend.Object, msg, false));

            Thread.Sleep(80);

            List<Message<LogisticsUpdate>> got = new List<Message<LogisticsUpdate>>();
            mq.ReadMessages(recipientId, antRecv.Object, Channel.DRONE_TASK_ASSIGNMENT, got, 10, true, PayloadType.LogisticsUpdate);
            Assert.AreEqual(0, got.Count);

            int dlq = GetDeadLetterQueueDepth(mq);
            Assert.IsTrue(dlq >= 1, "Expired RequiresAck message should be placed on dead-letter queue.");
        }

        [TestMethod]
        public void TaskAssignment_ProtoRoundTrip_PreciseWelding_And_Grinding()
        {
            Inventory weldInv = new Inventory();
            Task weld = new Task
            {
                TaskId = 1001,
                TaskType = TaskType.PreciseWelding,
                Position = new Vector3D(1, 2, 3),
                Payload = weldInv,
                AssignedBy = 55L
            };
            Task grind = new Task
            {
                TaskId = 1002,
                TaskType = TaskType.PreciseGrinding,
                Position = new Vector3D(4, 5, 6),
                AssignedBy = 55L
            };
            TaskAssignment original = new TaskAssignment { Tasks = new List<Task> { weld, grind } };

            ProtoBufMyUtilitiesDelegate util = new ProtoBufMyUtilitiesDelegate();
            byte[] bytes = util.SerializeToBinary((IMessagePayload)original);
            TaskAssignment roundTrip = util.SerializeFromBinary<TaskAssignment>(bytes);

            Assert.AreEqual(2, roundTrip.Tasks.Count);
            Assert.AreEqual(Scheduler.TaskType.PreciseWelding, roundTrip.Tasks[0].TaskType);
            Assert.AreEqual(Scheduler.TaskType.PreciseGrinding, roundTrip.Tasks[1].TaskType);
            Assert.AreEqual(1001u, roundTrip.Tasks[0].TaskId);
            Assert.AreEqual(1002u, roundTrip.Tasks[1].TaskId);
        }

        [TestMethod]
        public void SendMessage_Internal_Routing_Does_Not_Require_Antenna_For_Sender()
        {
            Mq mq = Mq.Instance;
            const long schedA = 801L;
            const long schedB = 802L;
            mq.Subscribe(schedB, Channel.DRONE_REPORTS);

            DroneReport rep = new DroneReport { DroneEntityId = 999L, Flags = Drone.UpdateFlags.Registration };
            Assert.AreEqual(ErrorCode.None, mq.SendMessage((ushort)Channel.DRONE_REPORTS, rep, schedA, false));

            Mock<IMyRadioAntenna> antB = CreateAntenna(1L, new Vector3D(0, 0, 0), 100f);
            mq.RegisterAntenna(schedB, Mq.IAIBlockType.Scheduler, antB.Object);

            List<Message<DroneReport>> got = new List<Message<DroneReport>>();
            Assert.AreEqual(ErrorCode.None, mq.ReadMessages(schedB, antB.Object, Channel.DRONE_REPORTS, got, 10, true, PayloadType.DroneReport));
            Assert.AreEqual(1, got.Count);
            Assert.AreEqual(999L, got[0].Payload.DroneEntityId);
        }

        private static int GetPendingAckCount(Mq mq, long senderId)
        {
            FieldInfo field = typeof(Mq).GetField("_pendingAcknowledgments", BindingFlags.Instance | BindingFlags.NonPublic);
            object dict = field.GetValue(mq);
            MethodInfo tryGet = dict.GetType().GetMethod("TryGetValue");
            object[] args = new object[] { senderId, null };
            bool ok = (bool)tryGet.Invoke(dict, args);
            if (!ok)
            {
                return 0;
            }
            object set = args[1];
            PropertyInfo countProp = set.GetType().GetProperty("Count");
            return (int)countProp.GetValue(set, null);
        }

        private static int GetDeadLetterQueueDepth(Mq mq)
        {
            FieldInfo chField = typeof(Mq).GetField("_channelQueues", BindingFlags.Instance | BindingFlags.NonPublic);
            object dict = chField.GetValue(mq);
            MethodInfo tryGet = dict.GetType().GetMethod("TryGetValue");
            object[] args = new object[] { Channel.DEAD_LETTER_QUEUE, null };
            bool ok = (bool)tryGet.Invoke(dict, args);
            if (!ok || args[1] == null)
            {
                return 0;
            }
            object queue = args[1];
            int n = 0;
            MethodInfo tryDequeue = queue.GetType().GetMethod("TryDequeue");
            object[] deqArg = new object[] { null };
            while ((bool)tryDequeue.Invoke(queue, deqArg))
            {
                n++;
            }
            return n;
        }
#pragma warning restore 618
    }
}
