using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Globalization;

using VRage;
using VRage.Collections;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace Automata.VirtualInventory
{
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class Inventory
    {
        [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
        public enum InventoryType : byte
        {
            None = 0,
            Components = 1,
            Blocks = 2
        }

        /// <summary>
        /// Subtype → quantity. Serialized as protobuf map; comparer is normalized on access (see <see cref="Items"/>).
        /// </summary>
        [ProtoMember(1)]
        private Dictionary<MyStringHash, int> _items;

        private Dictionary<MyStringHash, int> Items
        {
            get
            {
                if (_items == null)
                {
                    _items = new Dictionary<MyStringHash, int>(MyStringHash.Comparer);
                }
                else if (!object.ReferenceEquals(_items.Comparer, MyStringHash.Comparer))
                {
                    Dictionary<MyStringHash, int> next = new Dictionary<MyStringHash, int>(MyStringHash.Comparer);
                    foreach (KeyValuePair<MyStringHash, int> kv in _items)
                    {
                        next[kv.Key] = kv.Value;
                    }
                    _items = next;
                }
                return _items;
            }
        }

        public Inventory()
        {
            _items = new Dictionary<MyStringHash, int>(MyStringHash.Comparer);
        }

        public Inventory(Dictionary<string, int> inventory)
        {
            _items = new Dictionary<MyStringHash, int>(MyStringHash.Comparer);
            foreach (KeyValuePair<string, int> pair in inventory)
            {
                MyStringHash key = MyStringHash.GetOrCompute(pair.Key);
                int existing;
                _items.TryGetValue(key, out existing);
                _items[key] = existing + pair.Value;
            }
        }

        public Inventory(List<KVPair> inventory)
        {
            _items = new Dictionary<MyStringHash, int>(MyStringHash.Comparer);
            if (inventory == null)
            {
                return;
            }
            foreach (KVPair pair in inventory)
            {
                if (pair.Value <= 0)
                {
                    continue;
                }
                int existing;
                _items.TryGetValue(pair.Key, out existing);
                _items[pair.Key] = existing + pair.Value;
            }
        }

        public Inventory(Dictionary<MyStringHash, int> inventory)
        {
            _items = new Dictionary<MyStringHash, int>(MyStringHash.Comparer);
            MergeKeyValuePairs(_items, inventory);
        }

        /// <summary>
        /// Creates a copy of <paramref name="other"/>'s counts. If <paramref name="other"/> is null, the inventory is empty.
        /// </summary>
        public Inventory(Inventory other)
        {
            _items = new Dictionary<MyStringHash, int>(MyStringHash.Comparer);
            MergeKeyValuePairs(_items, other == null ? null : other.Items);
        }

        private static void MergeKeyValuePairs(Dictionary<MyStringHash, int> target, IEnumerable<KeyValuePair<MyStringHash, int>> source)
        {
            if (source == null)
            {
                return;
            }
            foreach (KeyValuePair<MyStringHash, int> pair in source)
            {
                int existing;
                target.TryGetValue(pair.Key, out existing);
                target[pair.Key] = existing + pair.Value;
            }
        }

        /// <summary>
        /// Human-readable label for logs or <see cref="Dictionary{TKey,TValue}"/> export; prefers registered string, else hash hex.
        /// </summary>
        public static string SubtypeKeyToString(MyStringHash key)
        {
            if (MyStringHash.IsKnown(key))
            {
                return key.String;
            }
            return "0x" + ((int)key).ToString("X8", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// True if both inventories list the same subtype keys with the same counts.
        /// </summary>
        public bool IsEqual(Inventory other)
        {
            if (object.ReferenceEquals(other, null))
            {
                return false;
            }
            if (object.ReferenceEquals(this, other))
            {
                return true;
            }
            Dictionary<MyStringHash, int> a = Items;
            Dictionary<MyStringHash, int> b = other.Items;
            if (a.Count != b.Count)
            {
                return false;
            }
            foreach (KeyValuePair<MyStringHash, int> kv in a)
            {
                int v;
                if (!b.TryGetValue(kv.Key, out v) || v != kv.Value)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Sets <paramref name="result"/> to this inventory minus <paramref name="other"/> per subtype:
        /// <c>max(0, this[k] - other[k])</c> for each key in this; keys only in <paramref name="other"/> are ignored.
        /// Does not modify this instance or <paramref name="other"/>. If <paramref name="other"/> is null, <paramref name="result"/> is a copy of this.
        /// </summary>
        public void Substract(Inventory other, out Inventory result)
        {
            result = new Inventory();
            Dictionary<MyStringHash, int> dst = result.Items;
            foreach (KeyValuePair<MyStringHash, int> kv in Items)
            {
                int sub = 0;
                if (other != null)
                {
                    other.Items.TryGetValue(kv.Key, out sub);
                }
                int diff = kv.Value - sub;
                if (diff > 0)
                {
                    dst[kv.Key] = diff;
                }
            }
        }

        /// <summary>
        /// Sets <paramref name="result"/> to this inventory minus <paramref name="other"/> per subtype:
        /// <c>max(0, this[k] - other[k])</c> for each key in this; keys only in <paramref name="other"/> are ignored.
        /// Does not modify this instance or <paramref name="other"/>. If <paramref name="other"/> is null, <paramref name="result"/> is a copy of this.
        /// </summary>
        public static Inventory Substract(Inventory inv, Inventory other)
        {
            Inventory result = new Inventory();
            Dictionary<MyStringHash, int> dst = result.Items;
            foreach (KeyValuePair<MyStringHash, int> kv in inv.Items)
            {
                int sub = 0;
                if (other != null)
                {
                    other.Items.TryGetValue(kv.Key, out sub);
                }
                int diff = kv.Value - sub;
                if (diff > 0)
                {
                    dst[kv.Key] = diff;
                }
            }
            return result;
        }

        /// <summary>
        /// Like <see cref="Substract(Inventory, out Inventory)"/>, but only subtypes that exist in both
        /// this and <paramref name="other"/> are included in <paramref name="result"/>; keys present only
        /// in this inventory are omitted. If <paramref name="other"/> is null, <paramref name="result"/> is empty.
        /// </summary>
        public void SubstractIntersect(Inventory other, out Inventory result)
        {
            result = new Inventory();
            if (other == null)
            {
                return;
            }
            Dictionary<MyStringHash, int> dst = result.Items;
            Dictionary<MyStringHash, int> otherItems = other.Items;
            foreach (KeyValuePair<MyStringHash, int> kv in Items)
            {
                int sub = 0;
                if (!otherItems.TryGetValue(kv.Key, out sub))
                {
                    continue;
                }
                int diff = kv.Value - sub;
                if (diff > 0)
                {
                    dst[kv.Key] = diff;
                }
            }
        }

        /// <summary>
        /// Like <see cref="Substract(Inventory)"/>, but only subtypes that exist in both inventories are
        /// retained; keys present only in this inventory are omitted. If <paramref name="other"/> is null, returns an empty inventory.
        /// </summary>
        public Inventory SubstractIntersect(Inventory other)
        {
            Inventory result;
            SubstractIntersect(other, out result);
            return result;
        }

        /// <summary>
        /// For each subtype in <paramref name="needed"/>, sets <paramref name="shortfall"/> to how much more
        /// is required to reach that amount from this inventory: <c>max(0, needed[k] - this[k])</c>.
        /// Subtypes only in this inventory are ignored. Does not modify this instance or <paramref name="needed"/>.
        /// If <paramref name="needed"/> is null, <paramref name="shortfall"/> is empty.
        /// </summary>
        public void ShortfallToSatisfy(Inventory needed, out Inventory shortfall)
        {
            if (needed == null)
            {
                shortfall = new Inventory();
                return;
            }
            needed.Substract(this, out shortfall);
        }

        /// <summary>
        /// Same as <see cref="ShortfallToSatisfy(Inventory, out Inventory)"/>, but returns the shortfall inventory.
        /// </summary>
        public Inventory ShortfallToSatisfy(Inventory needed)
        {
            Inventory shortfall;
            ShortfallToSatisfy(needed, out shortfall);
            return shortfall;
        }

        /// <summary>
        /// Sets <paramref name="filtered"/> to subtypes that appear in both this inventory and <paramref name="other"/>,
        /// using counts from this instance. Keys only in this or only in <paramref name="other"/> are omitted.
        /// Does not modify this instance or <paramref name="other"/>. If <paramref name="other"/> is null, <paramref name="filtered"/> is empty.
        /// </summary>
        public void FilterByKeys(Inventory other, out Inventory filtered)
        {
            filtered = new Inventory();
            if (other == null)
            {
                return;
            }
            Dictionary<MyStringHash, int> dst = filtered.Items;
            Dictionary<MyStringHash, int> otherItems = other.Items;
            foreach (KeyValuePair<MyStringHash, int> kv in Items)
            {
                if (otherItems.ContainsKey(kv.Key))
                {
                    dst[kv.Key] = kv.Value;
                }
            }
        }

        /// <summary>
        /// Same as <see cref="FilterByKeys(Inventory, out Inventory)"/>, but returns the filtered inventory.
        /// </summary>
        public Inventory FilterByKeys(Inventory other)
        {
            Inventory filtered;
            FilterByKeys(other, out filtered);
            return filtered;
        }

        public int GetItemCount(MyStringHash subtypeId)
        {
            int v;
            if (Items.TryGetValue(subtypeId, out v))
            {
                return v;
            }
            return 0;
        }

        public int GetItemCount(string subtypeId)
        {
            return GetItemCount(MyStringHash.GetOrCompute(subtypeId));
        }

        public void AddItem(string subtypeId, int amount)
        {
            if (amount <= 0)
            {
                return;
            }
            MyStringHash subtypeHash = MyStringHash.GetOrCompute(subtypeId);
            int cur;
            Items.TryGetValue(subtypeHash, out cur);
            Items[subtypeHash] = cur + amount;
        }

        public void AddItem(MyStringHash subtypeHash, int amount)
        {
            if (amount <= 0)
            {
                return;
            }
            int cur;
            Items.TryGetValue(subtypeHash, out cur);
            Items[subtypeHash] = cur + amount;
        }

        public void AddItems(Inventory inventory)
        {
            if (inventory == null)
            {
                return;
            }
            foreach (KeyValuePair<MyStringHash, int> kv in inventory.Items)
            {
                AddItem(kv.Key, kv.Value);
            }
        }

        public void AddItems(List<KVPair> items)
        {
            if (items == null || items.Count == 0)
            {
                return;
            }
            foreach (KVPair item in items)
            {
                if (item.Value <= 0)
                {
                    continue;
                }
                int cur;
                Items.TryGetValue(item.Key, out cur);
                Items[item.Key] = cur + item.Value;
            }
        }

        public void AddItems(IMyInventory sourceInventory)
        {
            if (sourceInventory == null)
            {
                return;
            }
            List<VRage.Game.ModAPI.Ingame.MyInventoryItem> items = new List<VRage.Game.ModAPI.Ingame.MyInventoryItem>();
            sourceInventory.GetItems(items);
            foreach (VRage.Game.ModAPI.Ingame.MyInventoryItem item in items)
            {
                string subtypeStr = item.Type.SubtypeId.ToString();
                int amount = (int)item.Amount;
                if (amount <= 0)
                {
                    continue;
                }
                MyStringHash subtypeHash = MyStringHash.GetOrCompute(subtypeStr);
                int cur;
                Items.TryGetValue(subtypeHash, out cur);
                Items[subtypeHash] = cur + amount;
            }
        }

        /// <summary>
        /// This overload adds all items from all inventories of the block.
        /// If the block has no inventories, this method does nothing.
        /// </summary>
        /// <param name="sourceBlock"></param>
        public void AddItems(IMyCubeBlock sourceBlock)
        {
            if (sourceBlock == null || !sourceBlock.HasInventory)
            {
                return;
            }
            for (int i = 0; i < sourceBlock.InventoryCount; i++)
            {
                IMyInventory sourceInventory = sourceBlock.GetInventory(i);
                if (sourceInventory == null)
                    continue;
                AddItems(sourceInventory);
            }
        }

        public bool Exists(string subtypeId)
        {
            return Items.ContainsKey(MyStringHash.GetOrCompute(subtypeId));
        }


        public bool RemoveItem(MyStringHash subtypeHash, int amount)
        {
            if (amount <= 0)
            {
                return false;
            }
            int cur;
            if (!Items.TryGetValue(subtypeHash, out cur))
            {
                return false;
            }
            if (cur <= amount)
            {
                Items.Remove(subtypeHash);
            }
            else
            {
                Items[subtypeHash] = cur - amount;
            }
            return true;
        }
        public bool RemoveItem(string subtypeId, int amount)
        {
            return RemoveItem(MyStringHash.GetOrCompute(subtypeId), amount);
        }

        public List<KVPair> GetAllItems()
        {
            List<KVPair> result = new List<KVPair>(Items.Count);
            foreach (KeyValuePair<MyStringHash, int> kv in Items)
            {
                result.Add(new KVPair { Key = kv.Key, Value = kv.Value });
            }
            return result;
        }

        public void GetAllItems(List<KVPair> result, bool clear = true)
        {
            if (clear)
                result.Clear();
            foreach (KeyValuePair<MyStringHash, int> kv in Items)
            {
                result.Add(new KVPair { Key = kv.Key, Value = kv.Value });
            }
        }

        public void GetAllItems(MyConcurrentList<KVPair> result, bool clear = true)
        {
            if (clear)
                result.Clear();
            foreach (KeyValuePair<MyStringHash, int> kv in Items)
            {
                result.Add(new KVPair { Key = kv.Key, Value = kv.Value });
            }
        }

        public void GetAllItems(Dictionary<string, int> result, bool clear = true)
        {
            if (clear)
                result.Clear();
            foreach (KeyValuePair<MyStringHash, int> kv in Items)
            {
                result.Add(SubtypeKeyToString(kv.Key), kv.Value);
            }
        }

        public void GetAllItems(MyConcurrentDictionary<string, int> result, bool clear = true)
        {
            if (clear)
            {
                result.Clear();
            }
            foreach (KeyValuePair<MyStringHash, int> kv in Items)
            {
                result.Add(SubtypeKeyToString(kv.Key), kv.Value);
            }
        }

        public int GetItemTypeCount()
        {
            return Items.Count;
        }

        public int GetTotalItemCount()
        {
            int total = 0;
            foreach (KeyValuePair<MyStringHash, int> kv in Items)
            {
                total += kv.Value;
            }
            return total;
        }

        public void Clear()
        {
            Items.Clear();
        }

        public bool IsEmpty()
        {
            return Items.Count == 0;
        }

        public bool ContainsAtLeast(Inventory required)
        {
            if (required == null || required.IsEmpty())
            {
                return true;
            }
            foreach (KeyValuePair<MyStringHash, int> req in required.Items)
            {
                if (req.Value <= 0)
                {
                    continue;
                }
                if (GetItemCount(req.Key) < req.Value)
                {
                    return false;
                }
            }
            return true;
        }



        /// <summary>
        /// Calculates the mass of the inventory. If the inventory is components, 
        /// </summary>
        /// <param name="session"></param>
        /// <returns></returns>
        public MyFixedPoint TotalInventoryMass(AutomataSession session = null)
        {
            var mySession = session ?? AutomataSession.Instance;
            MyFixedPoint totalMass = MyFixedPoint.Zero;
            foreach (KeyValuePair<MyStringHash, int> kv in Items)
            {
                ComponentData componentData;
                if (!mySession.ComponentLookup.TryGetValue(kv.Key, out componentData))
                {
                    continue;
                }
                totalMass += componentData.Mass * (MyFixedPoint)kv.Value;
            }
            return totalMass;
        }
        public MyFixedPoint TotalInventoryVolume(AutomataSession session = null)
        {
            var mySession = session ?? AutomataSession.Instance;
            MyFixedPoint totalMass = MyFixedPoint.Zero;
            foreach (KeyValuePair<MyStringHash, int> kv in Items)
            {
                ComponentData componentData;
                if (!mySession.ComponentLookup.TryGetValue(kv.Key, out componentData))
                {
                    continue;
                }
                totalMass += componentData.Volume * (MyFixedPoint)kv.Value;
            }
            return totalMass;
        }

        /// <summary>
        /// Subtracts <paramref name="amount"/> from <paramref name="sourceDict"/> and adds it to <paramref name="destinationDict"/>
        /// for <paramref name="key"/>. Assumes <paramref name="amount"/> &gt; 0 and the source had at least that many (caller validates).
        /// </summary>
        private static void TransferCounts(Dictionary<MyStringHash, int> sourceDict, Dictionary<MyStringHash, int> destinationDict, MyStringHash key, int amount)
        {
            int current;
            sourceDict.TryGetValue(key, out current);
            int afterSource = current - amount;
            if (afterSource <= 0)
            {
                sourceDict.Remove(key);
            }
            else
            {
                sourceDict[key] = afterSource;
            }
            int destinationCurrent;
            destinationDict.TryGetValue(key, out destinationCurrent);
            destinationDict[key] = destinationCurrent + amount;
        }

        /// <summary>
        /// Moves counts from <paramref name="source"/> to <paramref name="destination"/> for each subtype in
        /// <paramref name="itemsToMove"/> (amounts &gt; 0). Fails if <paramref name="source"/> does not
        /// contain at least those amounts. If <paramref name="itemsToMove"/> is the same instance as
        /// <paramref name="source"/> or <paramref name="destination"/>, entries are copied before transfer so
        /// the dictionaries are not modified during enumeration.
        /// </summary>
        public static bool MoveItems(Inventory source, Inventory destination, Inventory itemsToMove)
        {
            if (source == null || destination == null || itemsToMove == null)
            {
                return false;
            }
            Dictionary<MyStringHash, int> sourceDict = source.Items;
            Dictionary<MyStringHash, int> destinationDict = destination.Items;
            bool snapshotNeeded = object.ReferenceEquals(source, itemsToMove) || object.ReferenceEquals(destination, itemsToMove);

            if (snapshotNeeded)
            {
                List<KeyValuePair<MyStringHash, int>> plan = new List<KeyValuePair<MyStringHash, int>>(itemsToMove.GetItemTypeCount());
                foreach (KeyValuePair<MyStringHash, int> kv in itemsToMove.Items)
                {
                    if (kv.Value <= 0)
                    {
                        continue;
                    }
                    int available;
                    if (!sourceDict.TryGetValue(kv.Key, out available) || available < kv.Value)
                    {
                        return false;
                    }
                    plan.Add(kv);
                }
                for (int i = 0; i < plan.Count; i++)
                {
                    KeyValuePair<MyStringHash, int> kv = plan[i];
                    TransferCounts(sourceDict, destinationDict, kv.Key, kv.Value);
                }
            }
            else
            {
                foreach (KeyValuePair<MyStringHash, int> kv in itemsToMove.Items)
                {
                    if (kv.Value <= 0)
                    {
                        continue;
                    }
                    int available;
                    if (!sourceDict.TryGetValue(kv.Key, out available) || available < kv.Value)
                    {
                        return false;
                    }
                }
                foreach (KeyValuePair<MyStringHash, int> kv in itemsToMove.Items)
                {
                    if (kv.Value <= 0)
                    {
                        continue;
                    }
                    TransferCounts(sourceDict, destinationDict, kv.Key, kv.Value);
                }
            }
            return true;
        }

        public override string ToString()
        {
            if (Items.Count == 0)
            {
                return "Empty inventory";
            }
            List<string> parts = new List<string>(Items.Count);
            foreach (KeyValuePair<MyStringHash, int> kv in Items)
            {
                parts.Add(SubtypeKeyToString(kv.Key) + ": " + kv.Value.ToString(CultureInfo.InvariantCulture));
            }
            return string.Join(", ", parts);
        }
    }
}
