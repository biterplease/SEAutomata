using System;

namespace ImprovedAI.Util
{
    /// <summary>
    /// Thread-safe static ID generator that combines an entity ID prefix with a counter.
    /// Configurable bit distribution between entity ID and counter.
    /// </summary>
    public static class IdGenerator
    {
        /// <summary>
        /// Generate next unique ID for the specified entity
        /// </summary>
        /// <param name="counter">Reference to counter to increment</param>
        /// <param name="entityId">Unique entity ID (only lower N bits are used based on entityBits)</param>
        /// <param name="entityBits">Number of bits to use for entity ID (1-31, default 16)</param>
        /// <param name="interlockedDelegate">Interlocked operations delegate</param>
        /// <returns>Unique uint ID combining entity prefix and counter</returns>
        public static uint GenerateId(ref int counter, long entityId, int entityBits = 16, IInterlockedDelegate interlockedDelegate = null)
        {
            if (entityBits < 1 || entityBits > 31)
                throw new ArgumentException("Entity bits must be between 1 and 31", nameof(entityBits));

            var interlocked = interlockedDelegate ?? new InterlockedDelegate();
            int counterBits = 32 - entityBits;
            int maxCounter = (1 << counterBits) - 1;
            uint counterMask = (uint)maxCounter;

            ulong entityMask = (1UL << entityBits) - 1;
            uint extractedEntityId = (uint)((ulong)entityId & entityMask);
            uint entityPrefix = extractedEntityId << counterBits;

            int currentCounter = interlocked.Increment(ref counter);

            if (currentCounter > maxCounter)
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
            return $"[Entity:{ExtractEntityId(id, entityBits)}, Counter:{ExtractCounter(id, entityBits)}]";
        }
    }
}