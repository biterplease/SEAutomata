using ImprovedAI.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ImprovedAI.Tests.Util
{
    [TestClass]
    public class IdGeneratorTests
    {
        [TestMethod]
        public void Constructor_ValidEntityBits_InitializesCorrectly()
        {
            var generator = new IdGenerator(12345, 16);

            Assert.AreEqual<uint>(12345, generator.EntityId);
            Assert.AreEqual(16, generator.EntityBits);
            Assert.AreEqual(16, generator.CounterBits);
            Assert.AreEqual(65536, generator.MaxUniqueIds);
        }

        [TestMethod]
        public void Constructor_InvalidEntityBits_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => new IdGenerator(123, 0));
            Assert.Throws<ArgumentException>(() => new IdGenerator(123, 32));
            Assert.Throws<ArgumentException>(() => new IdGenerator(123, -1));
        }

        [TestMethod]
        public void Constructor_EntityIdExceedsBitMask_TruncatesToLowerBits()
        {
            // Entity ID 0xFFFF with 8 bits should truncate to 0xFF
            var generator = new IdGenerator(0xFFFF, 8);

            Assert.AreEqual<uint>(0xFF, generator.EntityId);
        }

        [TestMethod]
        public void GenerateId_FirstCall_ReturnsEntityPrefixPlusOne()
        {
            var generator = new IdGenerator(5, 16);
            uint id = generator.GenerateId();

            Assert.AreEqual<uint> (5, IdGenerator.ExtractEntityId(id, 16));
            Assert.AreEqual <uint>(1, IdGenerator.ExtractCounter(id, 16));
        }

        [TestMethod]
        public void GenerateId_MultipleCalls_IncrementsCounter()
        {
            var generator = new IdGenerator(10, 16);

            var id1 = generator.GenerateId();
            var id2 = generator.GenerateId();
            var id3 = generator.GenerateId();

            Assert.AreEqual<uint>(1, IdGenerator.ExtractCounter(id1, 16));
            Assert.AreEqual<uint>(2, IdGenerator.ExtractCounter(id2, 16));
            Assert.AreEqual<uint>(3, IdGenerator.ExtractCounter(id3, 16));

            // All should have same entity ID
            Assert.AreEqual<uint>(10, IdGenerator.ExtractEntityId(id1, 16));
            Assert.AreEqual<uint>(10, IdGenerator.ExtractEntityId(id2, 16));
            Assert.AreEqual<uint>(10, IdGenerator.ExtractEntityId(id3, 16));
        }

        [TestMethod]
        public void GenerateId_WrapsAroundAtMaxCounter()
        {
            var mockInterlocked = new MockInterlockedDelegate();
            var generator = new IdGenerator(7, 30, mockInterlocked); // 2 counter bits = max 3

            // Simulate counter at max
            mockInterlocked.SetValue(4); // Beyond max of 3

            var id = generator.GenerateId();

            // Should wrap back to 1
            Assert.AreEqual<uint>(1, IdGenerator.ExtractCounter(id, 30));
        }

        [TestMethod]
        public void GenerateId_AllIdsUnique_WithinMaxRange()
        {
            var generator = new IdGenerator(100, 24); // 8 counter bits = 256 unique IDs
            var ids = new HashSet<uint>();

            for (int i = 0; i < 256; i++)
            {
                uint id = generator.GenerateId();
                Assert.IsTrue(ids.Add(id), $"Duplicate ID generated: {id}");
            }
        }

        [TestMethod]
        public void ExtractEntityId_VariousBitConfigurations_ReturnsCorrectValue()
        {
            // 16 entity bits
            uint id16 = (123u << 16) | 456u;
            Assert.AreEqual<uint>(123, IdGenerator.ExtractEntityId(id16, 16));

            // 8 entity bits
            uint id8 = (45u << 24) | 789u;
            Assert.AreEqual<uint>(45, IdGenerator.ExtractEntityId(id8, 8));

            // 20 entity bits
            uint id20 = (1000u << 12) | 50u;
            Assert.AreEqual<uint>(1000, IdGenerator.ExtractEntityId(id20, 20));
        }

        [TestMethod]
        public void ExtractCounter_VariousBitConfigurations_ReturnsCorrectValue()
        {
            // 16 counter bits
            uint id16 = (123u << 16) | 456u;
            Assert.AreEqual<uint>(456, IdGenerator.ExtractCounter(id16, 16));

            // 24 counter bits
            uint id24 = (45u << 24) | 789u;
            Assert.AreEqual<uint>(789, IdGenerator.ExtractCounter(id24, 8));

            // 12 counter bits
            uint id12 = (1000u << 12) | 50u;
            Assert.AreEqual<uint>(50, IdGenerator.ExtractCounter(id12, 20));
        }

        [TestMethod]
        public void SameEntity_SameEntityIds_ReturnsTrue()
        {
            var generator = new IdGenerator(42, 16);
            var id1 = generator.GenerateId();
            var id2 = generator.GenerateId();

            Assert.IsTrue(IdGenerator.SameEntity(id1, id2, 16));
        }

        [TestMethod]
        public void SameEntity_DifferentEntityIds_ReturnsFalse()
        {
            var gen1 = new IdGenerator(10, 16);
            var gen2 = new IdGenerator(20, 16);

            var id1 = gen1.GenerateId();
            var id2 = gen2.GenerateId();

            Assert.IsFalse(IdGenerator.SameEntity(id1, id2, 16));
        }

        [TestMethod]
        public void ResetCounter_AfterGeneration_ResetsToZero()
        {
            var generator = new IdGenerator(5, 16);

            generator.GenerateId();
            generator.GenerateId();
            generator.GenerateId();

            generator.ResetCounter();

            Assert.AreEqual(0, generator.CurrentCounter);

            // Next ID should be 1 again
            var id = generator.GenerateId();
            Assert.AreEqual<uint>(1, IdGenerator.ExtractCounter(id, 16));
        }

        [TestMethod]
        public void FormatId_ReturnsReadableString()
        {
            var generator = new IdGenerator(42, 16);
            var id = generator.GenerateId();

            string formatted = IdGenerator.FormatId(id, 16);

            Assert.IsTrue(formatted.Contains("Entity:42"));
            Assert.IsTrue(formatted.Contains("Counter:1"));
        }

        [TestMethod]
        public void CurrentCounter_ReflectsGenerationCount()
        {
            var generator = new IdGenerator(1, 16);

            Assert.AreEqual(0, generator.CurrentCounter);

            generator.GenerateId();
            Assert.AreEqual(1, generator.CurrentCounter);

            generator.GenerateId();
            generator.GenerateId();
            Assert.AreEqual(3, generator.CurrentCounter);
        }

        [TestMethod]
        public void ThreadSafety_ConcurrentGeneration_NoCollisions()
        {
            var generator = new IdGenerator(999, 16);
            var ids = new HashSet<uint>();
            var lockObj = new object();
            const int threadCount = 10;
            const int idsPerThread = 100;

            var tasks = new Task[threadCount];

            for (int t = 0; t < threadCount; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    var localIds = new List<uint>();
                    for (int i = 0; i < idsPerThread; i++)
                    {
                        localIds.Add(generator.GenerateId());
                    }

                    lock (lockObj)
                    {
                        foreach (var id in localIds)
                        {
                            Assert.IsTrue(ids.Add(id), $"Collision detected: {id}");
                        }
                    }
                });
            }

            Task.WaitAll(tasks);
            Assert.AreEqual(threadCount * idsPerThread, ids.Count);
        }

        [TestMethod]
        public void EdgeCase_OneEntityBit_WorksCorrectly()
        {
            var generator = new IdGenerator(1, 1);

            Assert.AreEqual(1, generator.EntityBits);
            Assert.AreEqual(31, generator.CounterBits);
            Assert.AreEqual<uint>(1, generator.EntityId);
        }

        [TestMethod]
        public void EdgeCase_ThirtyOneEntityBits_WorksCorrectly()
        {
            var generator = new IdGenerator(123456, 31);

            Assert.AreEqual(31, generator.EntityBits);
            Assert.AreEqual(1, generator.CounterBits);
            Assert.AreEqual(2, generator.MaxUniqueIds);
        }

        [TestMethod]
        public void EntityIdExtraction_NegativeEntityId_HandlesCorrectly()
        {
            var generator = new IdGenerator(-1, 16);

            // -1 as long = 0xFFFFFFFFFFFFFFFF, lower 16 bits = 0xFFFF = 65535
            Assert.AreEqual<uint>(0xFFFF, generator.EntityId);
        }
    }

    // Mock delegate for testing wrap-around behavior
    public class MockInterlockedDelegate : IInterlockedDelegate
    {
        private int _value = 0;

        public void SetValue(int value) => _value = value;

        public int Increment(ref int location)
        {
            return Interlocked.Increment(ref _value);
        }

        public int CompareExchange(ref int location, int value, int comparand)
        {
            return Interlocked.CompareExchange(ref _value, value, comparand);
        }

        public int Exchange(ref int location, int value)
        {
            return Interlocked.Exchange(ref _value, value);
        }

        public T Exchange<T>(ref T location1, T value) where T : class
        {
            return Interlocked.Exchange(ref location1, value);
        }
    }
}