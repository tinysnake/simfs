using System;
using System.Buffers;

namespace SimFS
{
    internal struct BufferHolder<T> : IDisposable
    {
        private BufferHolder(T[] buffer, bool exactSize, Range range)
        {
            _buffer = buffer;
            _range = range;
            _exactSize = exactSize;
        }

        public BufferHolder(int minSize, bool exactSize)
        {
            if (minSize <= 0)
                throw new ArgumentException(nameof(minSize));
            _buffer = ArrayPool<T>.Shared.Rent(minSize);
#if SIMFS_POOLING_DEBUG
            SimLog.Info($"got buffer: {_buffer.GetHashCode()}");
#endif
            _exactSize = exactSize;
            if (exactSize)
                _range = new Range(0, minSize);
            else
                _range = new Range(0, _buffer.Length);
        }

        private T[] _buffer;
        private readonly Range _range;
        private readonly bool _exactSize;

        public readonly bool IsValid => _buffer != null;
        public readonly int Length => _exactSize ? _range.GetOffsetAndLength(_buffer.Length).Length : _buffer.Length;
        public readonly Span<T> Span => _exactSize ? _buffer.AsSpan()[_range] : _buffer;
        public readonly Memory<T> Memory => _exactSize ? _buffer.AsMemory()[_range] : _buffer;
        public readonly T[] Raw => _buffer;

        public BufferHolder<T> this[Range range]
        {
            get
            {
                var nbh = new BufferHolder<T>(_buffer, _exactSize, _range);
                _buffer = null;
                return nbh;
            }
        }

        public BufferHolder<T> Slice(int begin, int count)
        {
            if (begin < 0 || begin >= _buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(begin));
            if (count < 0 || begin + count > _buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            var nbh = new BufferHolder<T>(_buffer, _exactSize, new Range(begin, begin + count));
            _buffer = null;
            return nbh;
        }

        public void Dispose()
        {
            if (_buffer == null)
                throw new ObjectDisposedException(typeof(BufferHolder<T>).Name);
            ArrayPool<T>.Shared.Return(_buffer);
#if SIMFS_POOLING_DEBUG
            SimLog.Info($"return buffer: {_buffer.GetHashCode()}");
#endif
            _buffer = null;
        }

        public static implicit operator ReadOnlyMemory<T>(BufferHolder<T> holder) => holder.Memory;
        public static implicit operator ReadOnlySpan<T>(BufferHolder<T> holder) => holder.Span;
        public static implicit operator T[](BufferHolder<T> holder) => holder.Raw;
    }
}
