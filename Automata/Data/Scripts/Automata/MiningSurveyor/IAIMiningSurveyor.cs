using System;
using System.Collections.Generic;

using VRage;
using VRage.Collections;
using VRage.Game.ModAPI;
using VRageMath;
using VRage.ModAPI;
using VRage.Utils;
using Sandbox.ModAPI;

using Automata.Util;
using Automata.Util.Logging;
using Automata.VirtualNetwork;
using static Automata.MiningSurveyor;

namespace Automata.MiningSurveyor
{
    /// <summary>
    /// Indexes ore survey samples from drones and publishes <see cref="Orchestrator.JobType.MineOre"/> job announcements.
    /// </summary>
    public class IAIMiningSurveyor
    {
        private readonly IMyCubeBlock block;
        private readonly MessageQueue messaging;
        private readonly MyConcurrentQueue<Orchestrator.Job> jobQueue;
        private readonly MyConcurrentQueue<Orchestrator.Job> builtInParentJobQueue;
        private readonly OperationMode operationMode;
        public IAIMiningSurveyorSettings settings = new IAIMiningSurveyorSettings();

        private IMyRadioAntenna ownAntenna;
        private IMyGravityProviderSystemDelegate gravityProviderSystemDelegate;
        private readonly List<MiningDepositCacheEntry> cachedDeposits = new List<MiningDepositCacheEntry>();
        private readonly List<Message<MiningSurveyDataBroadcast>> miningBroadcastCache = new List<Message<MiningSurveyDataBroadcast>>();
        private readonly HashSet<MyStringHash> oreIgnoreHashes = new HashSet<MyStringHash>(MyStringHash.Comparer);
        private readonly OreMassInventory publishedMassTally = new OreMassInventory();
        private int jobIdCounter;
        private int messageCounter;
        private bool antennaRegistered;
        private Vector3 naturalGravity;

        public IAIMiningSurveyor(
            IMyEntity entity,
            MessageQueue messaging,
            OperationMode operationMode,
            MyConcurrentQueue<Orchestrator.Job> builtInParentJobQueue,
            IAIMiningSurveyorSettings settings,
            IMyGravityProviderSystemDelegate gravityProviderSystem = null)
        {
            this.block = (IMyCubeBlock)entity;
            this.messaging = messaging;
            this.operationMode = operationMode;
            this.builtInParentJobQueue = builtInParentJobQueue;
            this.jobQueue = new MyConcurrentQueue<Orchestrator.Job>();
            this.gravityProviderSystemDelegate = gravityProviderSystem ?? new MyGravityProviderSystemDelegate();
            if (settings != null)
            {
                this.settings = settings;
            }
        }

        /// <summary>
        /// Returns true if a new deposit at <paramref name="position"/> for <paramref name="ore"/> should be stored:
        /// no existing cached sample of the same ore within <see cref="MiningSurveyor.DuplicateDetectionRadiusMeters"/>.
        /// </summary>
        public static bool ShouldAddDeposit(
            IList<MiningDepositCacheEntry> existing,
            MyStringHash ore,
            Vector3D position,
            double duplicateRadiusSquared)
        {
            for (int i = 0; i < existing.Count; i++)
            {
                MiningDepositCacheEntry d = existing[i];
                if (d.OreSubtype != ore)
                {
                    continue;
                }
                if (Vector3D.DistanceSquared(d.Position, position) <= duplicateRadiusSquared)
                {
                    return false;
                }
            }
            return true;
        }

        public void Update(IMyRadioAntenna antenna)
        {
            ownAntenna = antenna;
            if (!settings.IsEnabled || messaging == null)
            {
                return;
            }

            if (!AntennaUtil.AntennaReady(ownAntenna))
            {
                return;
            }

            EnsureAntennaRegistration();
            SetGravity(block);
            RebuildOreIgnoreHashes();
            ReadMiningBroadcasts();
            ApplyIncomingSamples();
            TryEnqueueMineOreJobs();
            if (operationMode == OperationMode.MiningSurveyor)
            {
                HandlePublishingJobs();
            }
            else if (operationMode == OperationMode.BuiltInToOrchestrator && builtInParentJobQueue != null)
            {
                HandlePublishingJobs(builtInParentJobQueue);
            }
            else 
            {
                Log.Error("MiningSurveyor {0} invalid state: operation mode: {1}, builtInParentJobQueue: {2}", block.EntityId, operationMode, builtInParentJobQueue != null);
            }
        }

        private void EnsureAntennaRegistration()
        {
            if (antennaRegistered || ownAntenna == null)
            {
                return;
            }
            bool isStaticGrid = block != null && block.CubeGrid != null && block.CubeGrid.IsStatic;
            if (operationMode == OperationMode.BuiltInToOrchestrator)
            {
                messaging.RegisterAntenna(
                    block.EntityId,
                    MessageQueue.IAIBlockType.Orchestrator | MessageQueue.IAIBlockType.MiningSurveyor,
                    ownAntenna,
                    isStaticGrid);
            }
            else
            {
                messaging.RegisterAntenna(block.EntityId, MessageQueue.IAIBlockType.MiningSurveyor, ownAntenna, isStaticGrid);
            }
            messaging.Subscribe(block.EntityId, Channel.MINING_DATA_FOUND_ANNOUNCEMENT);
            antennaRegistered = true;
        }

        private void SetGravity(IMyCubeBlock cubeBlock)
        {
            if (cubeBlock == null)
            {
                naturalGravity = Vector3.Zero;
                return;
            }
            Vector3D blockPosition = cubeBlock.CubeGrid.GridIntegerToWorld(cubeBlock.Position);
            naturalGravity = gravityProviderSystemDelegate.CalculateNaturalGravityInPoint(blockPosition);
        }

        private void RebuildOreIgnoreHashes()
        {
            oreIgnoreHashes.Clear();
            if (settings.OreIgnoreList == null)
            {
                return;
            }
            for (int i = 0; i < settings.OreIgnoreList.Count; i++)
            {
                string name = settings.OreIgnoreList[i];
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }
                oreIgnoreHashes.Add(MyStringHash.GetOrCompute(name));
            }
        }

        private void ReadMiningBroadcasts()
        {
            miningBroadcastCache.Clear();
            messaging.ReadMessages(
                block.EntityId,
                ownAntenna,
                Channel.MINING_DATA_FOUND_ANNOUNCEMENT,
                miningBroadcastCache,
                50,
                true,
                PayloadType.MiningSurveyDataBroadcast);
        }

        private void ApplyIncomingSamples()
        {
            Vector3D origin = ownAntenna.GetPosition();
            double antennaRange = ownAntenna.Radius;
            double antennaRangeSq = antennaRange * antennaRange;
            float customRange = settings.IgnoreSamplesOutsideSpecifiedRangeMeters;
            double customRangeSq = customRange * customRange;

            for (int m = 0; m < miningBroadcastCache.Count; m++)
            {
                MiningSurveyDataBroadcast payload = miningBroadcastCache[m].Payload;
                if (payload == null || payload.Samples == null)
                {
                    continue;
                }
                for (int s = 0; s < payload.Samples.Count; s++)
                {
                    MiningOreDepositSample sample = payload.Samples[s];
                    TryAddSample(sample, origin, antennaRangeSq, customRangeSq);
                }
            }
        }

        private void TryAddSample(MiningOreDepositSample sample, Vector3D origin, double antennaRangeSq, double customRangeSq)
        {
            MyStringHash ore = sample.OreSubtype;
            if (!MyStringHash.IsKnown(ore))
            {
                return;
            }
            if (oreIgnoreHashes.Contains(ore))
            {
                return;
            }
            Vector3D pos = sample.Position.ToVector3D();
            if (settings.IgnoreSamplesOutsideOfAntennaRange)
            {
                if (Vector3D.DistanceSquared(origin, pos) > antennaRangeSq)
                {
                    return;
                }
            }
            if (settings.IgnoreSamplesOutsideSpecifiedRange)
            {
                if (Vector3D.DistanceSquared(origin, pos) > customRangeSq)
                {
                    return;
                }
            }
            if (!ShouldAddDeposit(cachedDeposits, ore, pos, DuplicateDetectionRadiusSquared))
            {
                return;
            }
            cachedDeposits.Add(new MiningDepositCacheEntry
            {
                OreSubtype = ore,
                Position = pos,
                EstimatedMass = sample.EstimatedMass,
                PendingPublish = true,
            });
        }

        private void TryEnqueueMineOreJobs()
        {
            if ((settings.WorkModes & WorkModes.ScanAndPublish) == 0)
            {
                return;
            }
            int budget = settings.MaxJobAnnouncementsPerUpdate;
            for (int i = 0; i < cachedDeposits.Count && budget > 0; i++)
            {
                MiningDepositCacheEntry d = cachedDeposits[i];
                if (!d.PendingPublish)
                {
                    continue;
                }
                if (!PassesQuotaAndLimits(d))
                {
                    d.PendingPublish = false;
                    continue;
                }
                Orchestrator.Job job = CreateMineOreJob(d);
                jobQueue.Enqueue(job);
                TallyPublishedMass(d.OreSubtype, settings.NominalMassPerMineOreJob);
                d.PendingPublish = false;
                budget--;
            }
        }

        private bool PassesQuotaAndLimits(MiningDepositCacheEntry d)
        {
            if (settings.UseOreRequests)
            {
                MyFixedPoint req = settings.OreRequests.GetMass(d.OreSubtype);
                if (req <= (MyFixedPoint)0f)
                {
                    return false;
                }
            }
            if (settings.UseOreLimits)
            {
                MyFixedPoint lim = settings.OreLimits.GetMass(d.OreSubtype);
                if (lim > (MyFixedPoint)0f)
                {
                    MyFixedPoint already = publishedMassTally.GetMass(d.OreSubtype);
                    if (already + settings.NominalMassPerMineOreJob > lim)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private void TallyPublishedMass(MyStringHash ore, MyFixedPoint delta)
        {
            publishedMassTally.AddMass(ore, delta);
        }

        private Orchestrator.Job CreateMineOreJob(MiningDepositCacheEntry d)
        {
            Inventory payload = new Inventory();
            int massUnits = SafeMassToInventoryUnits(settings.NominalMassPerMineOreJob);
            payload.AddItem(d.OreSubtype, massUnits);
            Vector3 gravityNorm = naturalGravity;
            float g = gravityNorm.Length();
            if (g > 1e-6f)
            {
                gravityNorm /= g;
            }
            float naturalG = g;
            bool isInSpace = naturalG < 0.2f;
            QuaternionD orientation = QuaternionD.Identity;
            return new Orchestrator.Job
            {
                JobId = IdGenerator.GenerateId(ref jobIdCounter, block.EntityId),
                JobType = Orchestrator.JobType.MineOre,
                ComponentsInventory = payload,
                BlocksInventory = new Inventory(),
                Tasks = new List<Orchestrator.Task>(),
                PositionData = Vector3DData.FromVector3D(d.Position),
                OrientationData = QuaternionDData.FromQuaternionD(orientation),
                CreatedTime = DateTime.UtcNow,
                NaturalGravity = naturalG,
                IsStaticGrid = block.CubeGrid != null && block.CubeGrid.IsStatic,
                IsInSpace = isInSpace,
                IsInAtmosphere = !isInSpace && naturalG > 0.2f,
                OutOfOrchestratorRange = false,
            };
        }

        /// <summary>
        /// Maps nominal ore mass to integer inventory units for <see cref="Inventory"/> (mass-like scalar per subtype).
        /// </summary>
        public static int SafeMassToInventoryUnits(MyFixedPoint mass)
        {
            float f = (float)mass;
            if (f <= 0f || float.IsNaN(f) || float.IsInfinity(f))
            {
                return 1;
            }
            int scaled = (int)(f * 1000f);
            if (scaled < 1)
            {
                scaled = 1;
            }
            if (scaled > int.MaxValue / 4)
            {
                scaled = int.MaxValue / 4;
            }
            return scaled;
        }

        public void HandlePublishingJobs()
        {
            if (operationMode != OperationMode.MiningSurveyor)
            {
                return;
            }
            if (!AntennaUtil.AntennaReady(ownAntenna))
            {
                return;
            }
            for (int i = 0; i < settings.MaxJobAnnouncementsPerUpdate; i++)
            {
                Orchestrator.Job job;
                if (!jobQueue.TryDequeue(out job))
                {
                    break;
                }
                Message<JobAnnouncement> msg = new Message<JobAnnouncement>
                {
                    Payload = new JobAnnouncement
                    {
                        JobId = job.JobId,
                        Type = job.JobType,
                        ComponentsInventory = job.ComponentsInventory,
                        BlocksInventory = job.BlocksInventory,
                        PositionData = job.PositionData,
                        OrientationData = job.OrientationData,
                        CreatedTime = job.CreatedTime,
                        EntityType = IAIEntityType.MiningSurveyorBlock,
                        NaturalGravity = job.NaturalGravity,
                        IsStaticGrid = job.IsStaticGrid,
                        IsInSpace = job.IsInSpace,
                        IsInAtmosphere = job.IsInAtmosphere,
                    },
                    MessageId = IdGenerator.GenerateId(ref messageCounter, block.EntityId),
                    CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                    SenderId = block.EntityId,
                    SenderOwnerId = block != null ? block.OwnerId : 0,
                    RequiresAck = false,
                    RecipientBlockType = MessageQueue.IAIBlockType.Orchestrator,
                    Channel = Channel.CONSTRUCTION_COMPUTER_JOB_ANNOUNCEMENT,
                };
                messaging.BroadcastMessage(ownAntenna, msg, true);
            }
        }

        public void HandlePublishingJobs(MyConcurrentQueue<Orchestrator.Job> parentJobQueue)
        {
            if (operationMode != OperationMode.BuiltInToOrchestrator || parentJobQueue == null)
            {
                return;
            }
            for (int i = 0; i < settings.MaxJobAnnouncementsPerUpdate; i++)
            {
                Orchestrator.Job job;
                if (!jobQueue.TryDequeue(out job))
                {
                    break;
                }
                parentJobQueue.Enqueue(job);
            }
        }
    }
}
