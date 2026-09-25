using System;
using System.Collections.Generic;
using ProtoBuf;

using VRage;
using VRage.Utils;

namespace Automata.VirtualInventory
{
    /// <summary>
    /// Ore amounts by subtype using <see cref="MyFixedPoint"/> mass, matching Space Engineers inventory precision
    /// (VRage fixed-point; sub-gram resolution, typically 0.0001 kg granularity for display and transfers).
    /// </summary>
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class OreMassInventory
    {
        /// <summary>
        /// Subtype → mass. Serialized as protobuf map.
        /// </summary>
        [ProtoMember(1)]
        private Dictionary<MyStringHash, MyFixedPoint> _items;

        private Dictionary<MyStringHash, MyFixedPoint> Items
        {
            get
            {
                if (_items == null)
                {
                    _items = new Dictionary<MyStringHash, MyFixedPoint>(MyStringHash.Comparer);
                }
                else if (!object.ReferenceEquals(_items.Comparer, MyStringHash.Comparer))
                {
                    Dictionary<MyStringHash, MyFixedPoint> next = new Dictionary<MyStringHash, MyFixedPoint>(MyStringHash.Comparer);
                    foreach (KeyValuePair<MyStringHash, MyFixedPoint> kv in _items)
                    {
                        next[kv.Key] = kv.Value;
                    }
                    _items = next;
                }
                return _items;
            }
        }

        public OreMassInventory()
        {
            _items = new Dictionary<MyStringHash, MyFixedPoint>(MyStringHash.Comparer);
        }

        public OreMassInventory(OreMassInventory other)
        {
            _items = new Dictionary<MyStringHash, MyFixedPoint>(MyStringHash.Comparer);
            if (other == null)
            {
                return;
            }
            foreach (KeyValuePair<MyStringHash, MyFixedPoint> kv in other.Items)
            {
                _items[kv.Key] = kv.Value;
            }
        }

        public void Clear()
        {
            Items.Clear();
        }

        public bool ContainsSubtype(MyStringHash subtype)
        {
            return Items.ContainsKey(subtype);
        }

        public MyFixedPoint GetMass(MyStringHash subtype)
        {
            MyFixedPoint v;
            if (!Items.TryGetValue(subtype, out v))
            {
                return (MyFixedPoint)0f;
            }
            return v;
        }

        public void SetMass(MyStringHash subtype, MyFixedPoint mass)
        {
            if (mass <= (MyFixedPoint)0f)
            {
                Items.Remove(subtype);
                return;
            }
            Items[subtype] = mass;
        }

        public void AddMass(MyStringHash subtype, MyFixedPoint delta)
        {
            if (delta == (MyFixedPoint)0f)
            {
                return;
            }
            MyFixedPoint existing;
            Items.TryGetValue(subtype, out existing);
            MyFixedPoint sum = existing + delta;
            if (sum <= (MyFixedPoint)0f)
            {
                Items.Remove(subtype);
            }
            else
            {
                Items[subtype] = sum;
            }
        }

        /// <summary>
        /// True if <paramref name="subtype"/> should be ignored (present with any positive mass entry).
        /// </summary>
        public bool IsIgnored(MyStringHash subtype, OreMassInventory ignoreList)
        {
            if (ignoreList == null)
            {
                return false;
            }
            return ignoreList.GetMass(subtype) > (MyFixedPoint)0f;
        }

        public Dictionary<MyStringHash, MyFixedPoint>.Enumerator GetEnumerator()
        {
            return Items.GetEnumerator();
        }
    }
}
