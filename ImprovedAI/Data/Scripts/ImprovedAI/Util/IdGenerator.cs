using System;

namespace ImprovedAI.Util
{
    /// <summary>
    /// Thread-safe ID generator that combines an entity ID prefix with a counter.
    /// Configurable bit distribution between entity ID and counter.
    /// Can be used as an instance (per-entity generator) or via static helpers.
    /// </summary>
    public class IdGenerator
    {
        private int _counter = 0;
        private readonly IInterlockedDelegate _interlocked;

        public uint EntityId { get; private set; }
        public int EntityBits { get; private set; }
        public int CounterBits { get; private set; }
        public int MaxUniqueIds { get; private set; }
        public int CurrentCounter => _counter;

        public IdGenerator(long entityId, int entityBits = 16, IInterlockedDelegate interlockedDelegate = null)
        {
            if (entityBits < 1 || entityBits > 31)
                throw new ArgumentException("Entity bits must be between 1 and 31", "entityBits");

            _interlocked = interlockedDelegate ?? new InterlockedDelegate();
            EntityBits = entityBits;
            CounterBits = 32 - entityBits;
            // MaxUniqueIds = 2^counterBits (0..maxCounter-1 plus the counter==maxCounter slot maps to 0 via mask)
            MaxUniqueIds = 1 << CounterBits;

            ulong entityMask = (1UL << entityBits) - 1;
            EntityId = (uint)((ulong)entityId & entityMask);
        }

        /// <summary>Generate the next unique ID for this entity.</summary>
        public uint GenerateId()
        {
            return GenerateId(ref _counter, (long)EntityId, EntityBits, _interlocked);
        }

        public void ResetCounter()
        {
            _interlocked.Exchange(ref _counter, 0);
        }

        // ----------------------------------------------------------------
        // Static API — kept for callers that maintain their own counter ref
        // ----------------------------------------------------------------

        /// <summary>
        /// Counter wraps via bitmask: counter values 1..(2^counterBits - 1) then 0 on the 2^counterBits-th call,
        /// yielding exactly 2^counterBits unique values per entity.
        /// </summary>
        public static uint GenerateId(ref int counter, long entityId, int entityBits = 16, IInterlockedDelegate interlockedDelegate = null)
        {
            if (entityBits < 1 || entityBits > 31)
                throw new ArgumentException("Entity bits must be between 1 and 31", "entityBits");

            var interlocked = interlockedDelegate ?? new InterlockedDelegate();
            int counterBits = 32 - entityBits;
            // wrapPoint is the exclusive upper bound; wrap via mask so counter 2^n maps to 0
            int wrapPoint = 1 << counterBits;
            uint counterMask = (uint)(wrapPoint - 1);

            ulong entityMask = (1UL << entityBits) - 1;
            uint extractedEntityId = (uint)((ulong)entityId & entityMask);
            uint entityPrefix = extractedEntityId << counterBits;

            int currentCounter = interlocked.Increment(ref counter);

            if (currentCounter > wrapPoint)
            {
                interlocked.CompareExchange(ref counter, 0, currentCounter);
                currentCounter = interlocked.Increment(ref counter);
            }

            return entityPrefix | ((uint)currentCounter & counterMask);
        }

        public static uint ExtractEntityId(uint id, int entityBits = 16)
        {
            return id >> (32 - entityBits);
        }

        public static uint ExtractCounter(uint id, int entityBits = 16)
        {
            int counterBits = 32 - entityBits;
            return id & (uint)((1 << counterBits) - 1);
        }

        public static bool SameEntity(uint id1, uint id2, int entityBits = 16)
        {
            return ExtractEntityId(id1, entityBits) == ExtractEntityId(id2, entityBits);
        }

        public static string FormatId(uint id, int entityBits = 16)
        {
            return string.Format("[Entity:{0}, Counter:{1}]", ExtractEntityId(id, entityBits), ExtractCounter(id, entityBits));
        }
    }
}
