using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SimFS
{
    public class Pooling
    {
        private const int MIN_CACHE_FILE_SIZE = 100;
        private const int MIN_CACHE_BLOCK_SIZE = 100;
        private const int MIN_BUFFER_SIZE = 1024;

        public Pooling(int blockSize)
        {
            IntListPool = new CrudeObjectPool<List<int>>(() => new List<int>(), onReturn: x => x.Clear(), maxCapacity: blockSize * 8);
            BlockGroupPool = new CrudeObjectPool<BlockGroup>(() => new BlockGroup(), onReturn: x => x.InPool(), maxCapacity: MIN_CACHE_BLOCK_SIZE);
            FileStreamPool = new CrudeObjectPool<SimFileStream>(() => new SimFileStream(), onReturn: x => x.InPool(), maxCapacity: MIN_CACHE_FILE_SIZE);
            DirectoryPool = new CrudeObjectPool<SimDirectory>(() => new SimDirectory(), onReturn: x => x.InPool(), maxCapacity: MIN_CACHE_FILE_SIZE);
            BlockPointersPool = new CrudeObjectPool<BlockPointerData[]>(() => new BlockPointerData[BlockPointersCount], onReturn: InodeData.OnBlockPointerInPool, maxCapacity: blockSize * FSHeadData.GetPointersSize((uint)blockSize));
            AttributesPool = new CrudeObjectPool<byte[]>(() => new byte[AttributeSize], onReturn: InodeData.OnAttributesInPool, maxCapacity: blockSize * 8);
        }

        private int _maxBufferSize = MIN_BUFFER_SIZE;
        public int MaxBufferSize
        {
            get => _maxBufferSize;
            set
            {
                if (value < MIN_BUFFER_SIZE)
                    value = MIN_BUFFER_SIZE;
                _maxBufferSize = value;
            }
        }

        internal int BlockPointersCount { get; set; }
        internal int AttributeSize { get; set; }
        internal IObjectPool<List<int>> IntListPool { get; private set; }
        internal IObjectPool<BlockGroup> BlockGroupPool { get; private set; }
        internal IObjectPool<SimFileStream> FileStreamPool { get; private set; }
        internal IObjectPool<SimDirectory> DirectoryPool { get; private set; }
        internal IObjectPool<BlockPointerData[]> BlockPointersPool { get; private set; }
        internal IObjectPool<byte[]> AttributesPool { get; private set; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal BufferHolder<byte> RentBuffer(out Span<byte> span, int minSize = 0)
        {
            if (minSize > _maxBufferSize || minSize <= 0)
                minSize = _maxBufferSize;
            var holder = new BufferHolder<byte>(minSize);
            span = holder.Span;
            return holder;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal BufferHolder<char> RentStringBuffer(out Span<char> span)
        {
            var holder = new BufferHolder<char>(256);
            span = holder.Span;
            return holder;
        }
    }
}
