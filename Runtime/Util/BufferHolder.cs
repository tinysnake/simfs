using System;
using System.Buffers;

namespace SimFS
{
    internal readonly struct BufferHolder<T> : IDisposable
    {
        public BufferHolder(int minSize)
        {
            _buffer = ArrayPool<T>.Shared.Rent(minSize);
        }

        private readonly T[] _buffer;

        public Span<T> Span => _buffer;
        public Memory<T> Memory => _buffer;
        public T[] Raw => _buffer;

        public void Dispose()
        {
            ArrayPool<T>.Shared.Return(_buffer);
        }

        public static implicit operator ReadOnlyMemory<T>(BufferHolder<T> holder) => holder.Memory;
        public static implicit operator ReadOnlySpan<T>(BufferHolder<T> holder) => holder.Span;
        public static implicit operator T[](BufferHolder<T> holder) => holder.Raw;
    }
}
