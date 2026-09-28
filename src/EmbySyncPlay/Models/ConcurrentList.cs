using System;
using System.Collections;
using System.Collections.Generic;

namespace EmbySyncPlay.Models
{
    /// <summary>Zárral védett lista, pillanatkép-alapú bejárással: a párhuzamos Add/Remove
    /// és foreach nem dob InvalidOperationException-t. Json.NET IList-ként kezeli.</summary>
    public class ConcurrentList<T> : IList<T>
    {
        private readonly List<T> _items = new List<T>();
        private readonly object _lock = new object();

        public int Count { get { lock (_lock) return _items.Count; } }
        public bool IsReadOnly => false;

        public T this[int index]
        {
            get { lock (_lock) return _items[index]; }
            set { lock (_lock) _items[index] = value; }
        }

        public void Add(T item) { lock (_lock) _items.Add(item); }
        public void Clear() { lock (_lock) _items.Clear(); }
        public bool Contains(T item) { lock (_lock) return _items.Contains(item); }
        public int IndexOf(T item) { lock (_lock) return _items.IndexOf(item); }
        public void Insert(int index, T item) { lock (_lock) _items.Insert(index, item); }
        public bool Remove(T item) { lock (_lock) return _items.Remove(item); }
        public void RemoveAt(int index) { lock (_lock) _items.RemoveAt(index); }
        public int RemoveAll(Predicate<T> match) { lock (_lock) return _items.RemoveAll(match); }
        public T Find(Predicate<T> match) { lock (_lock) return _items.Find(match); }
        public void CopyTo(T[] array, int arrayIndex) { lock (_lock) _items.CopyTo(array, arrayIndex); }

        public IEnumerator<T> GetEnumerator()
        {
            List<T> snapshot;
            lock (_lock) snapshot = new List<T>(_items);
            return snapshot.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
