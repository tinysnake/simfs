using System;
using System.Collections.Generic;

namespace SimFS
{
    internal class CrudeObjectPool<T> : IObjectPool<T>
    {
        public CrudeObjectPool(Func<T> onCreate, Action<T> onGet = null, Action<T> onReturn = null, Action<T> onDispose = null, int maxCapacity = 1000)
        {
            _list = new List<T>();
            _maxCapacity = maxCapacity;
            _onCreate = onCreate ?? throw new NullReferenceException(nameof(onCreate));
            _onGet = onGet;
            _onReturn = onReturn;
            _onDispose = onDispose;
        }

        private int _maxCapacity;
        private readonly Func<T> _onCreate;
        private readonly Action<T> _onGet;
        private readonly Action<T> _onReturn;
        private readonly Action<T> _onDispose;

        private readonly List<T> _list;


        public bool HasItem => _list.Count > 0;

        public int MaxCapacity
        {
            get => _maxCapacity;
            set
            {
                if (value <= 0)
                    throw new System.ArgumentOutOfRangeException(nameof(value));
                _maxCapacity = value;
                if (_maxCapacity < _list.Count)
                {
                    if (_onDispose != null)
                    {
                        while (_list.Count > _maxCapacity)
                        {
                            _onDispose?.Invoke(_list[^1]);
                            _list.RemoveAt(_list.Count - 1);
                        }
                    }
                    else
                        _list.RemoveRange(_maxCapacity, _list.Count - _maxCapacity);
                }
            }
        }

        public T Get()
        {
            T item;
            if (_list.Count > 0)
            {
                item = _list[^1];
                _list.RemoveAt(_list.Count - 1);
            }
            else
            {
                item = _onCreate.Invoke();
                if (EqualityComparer<T>.Default.Equals(item, default))
                    throw new ArgumentNullException(nameof(item));
            }
            _onGet?.Invoke(item);
            return item;
        }

        public void Return(T obj)
        {
#if SIMFS_POOLING_DEBUG
            if (_list.Contains(obj))
                throw new System.InvalidOperationException("you're returing a same obj twice!");
#endif
            _onReturn?.Invoke(obj);
            if (_list.Count >= _maxCapacity)
            {
                _onDispose?.Invoke(obj);
                return;
            }
            _list.Add(obj);
        }

        public void Clear()
        {
            foreach (var item in _list)
            {
                _onDispose?.Invoke(item);
            }
            _list.Clear();
        }
    }
}
