using Neo.Cryptography.ECC;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Neo.Plugins
{
    public class ResponseCollection : IEnumerable<ResponseItem>
    {
        private readonly Dictionary<ECPoint, ResponseItem> _items = new Dictionary<ECPoint, ResponseItem>();
        public int Count => _items.Count;

        public bool Add(ResponseItem item)
        {
            if (_items.TryGetValue(item.OraclePub, out var prev))
            {
                if (prev.Timestamp > item.Timestamp) return false;
                _items[item.OraclePub] = item;
                return true;
            }
            if (_items.TryAdd(item.OraclePub, item)) return true;
            return false;
        }

        public bool RemoveOutOfDateResponseItem(ECPoint[] publicKeys)
        {
            List<ECPoint> temp = new List<ECPoint>();
            foreach (var item in _items)
            {
                if (!publicKeys.Contains(item.Key))
                {
                    temp.Add(item.Key);
                }
            }
            foreach (var e in temp)
            {
                _items.Remove(e, out _);
            }
            return true;
        }

        public IEnumerator<ResponseItem> GetEnumerator()
        {
            return (IEnumerator<ResponseItem>)_items.Select(u => u.Value).ToArray().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }
    }
}
