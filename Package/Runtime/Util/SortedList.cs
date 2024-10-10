using System.Collections;
using System.Collections.Generic;

namespace SimFS
{
    internal enum SortedListInsertPolicy
    {
        AsIs,
        OnFirst,
        OnLast,
    }
    internal class SortedList<T> : IList<T>, IEnumerable<T>, IReadOnlyList<T>
    {
        public SortedList(IComparer<T> comparer = null)
        {
            _list = new List<T>();
            _comparer = comparer ?? Comparer<T>.Default;
        }

        public SortedList(IEnumerable<T> source, IComparer<T> comparer = null)
            : this(comparer)
        {
            foreach (var item in source)
            {
                Add(item);
            }
        }

        private readonly List<T> _list;
        private readonly IComparer<T> _comparer;

        public SortedListInsertPolicy InsertPolicyOnSameOrder { get; set; }

        public T this[int index] => _list[index];

        T IList<T>.this[int index] { get => _list[index]; set => throw new System.NotSupportedException(); }

        public int Count => _list.Count;

        int ICollection<T>.Count => _list.Count;

        bool ICollection<T>.IsReadOnly => false;

        public List<T>.Enumerator GetEnumerator() => _list.GetEnumerator();

        int IList<T>.IndexOf(T item) => IndexOf(item, true);

        public int IndexOf(T item, bool exactMatch)
        {
            if (!exactMatch)
                return _list.BinarySearch(item, _comparer);

            var i = _list.BinarySearch(item, _comparer);
            if (i < 0)
                return i;
            return GetActualIndex(item, i);
        }

        private int GetActualIndex(T item, int index)
        {
            var left = index - 1;
            while (left-- > 0)
            {
                if (EqualityComparer<T>.Default.Equals(_list[left], item))
                    return left;
            }
            do
            {
                if (EqualityComparer<T>.Default.Equals(_list[index], item))
                    return index;
            }
            while (index++ < _list.Count - 1);
            return -1;
        }

        public void Add(T item)
        {
            var index = _list.BinarySearch(item, _comparer);
            if (index < 0)
            {
                _list.Insert(~index, item);
            }
            else
            {
                var flag = InsertPolicyOnSameOrder switch
                {
                    SortedListInsertPolicy.OnFirst => -1,
                    SortedListInsertPolicy.OnLast => 1,
                    _ => 0,
                };
                if (flag == 0)
                    _list.Insert(index, item);
                else
                {
                    index += flag;
                    while (index >= 0 && index < _list.Count)
                    {
                        if (_comparer.Compare(_list[index], item) != 0)
                        {
                            _list.Insert(index, item);
                            return;
                        }
                        index += flag;
                    }
                    if (index < 0)
                        _list.Insert(0, item);
                    else if (index >= _list.Count)
                        _list.Add(item);
                }
            }
        }

        public bool Remove(T item)
        {
            var index = IndexOf(item, true);
            if (index < 0)
                return false;
            _list.RemoveAt(index);
            return true;
        }

        public void RemoveAt(int index)
        {
            _list.RemoveAt(index);
        }

        public void Clear()
        {
            _list.Clear();
        }

        public bool Contains(T item)
        {
            return _list.BinarySearch(item, _comparer) >= 0;
        }

        public void DangerouslySetIndex(int index, T item)
        {
            _list[index] = item;
        }

        void IList<T>.Insert(int index, T item)
        {
            throw new System.NotSupportedException();
        }

        void ICollection<T>.CopyTo(T[] array, int arrayIndex)
        {
            _list.CopyTo(array, arrayIndex);
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
