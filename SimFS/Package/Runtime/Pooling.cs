using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SimFS
{
    internal class Pooling
    {
        private const int MIN_CACHE_FILE_SIZE = 100;
        private const int MIN_CACHE_BLOCK_SIZE = 100;
        private const int MIN_BUFFER_SIZE = 1024;
        private const int DEFAULT_BLOCK_SIZE = 1024;

        public Pooling(Customizer customizer)
        {
            _maxBufferSize = customizer.BufferSize;
            BlockSize = DEFAULT_BLOCK_SIZE;
            TransactionPooling = new TransactionPooling(customizer.TransactionsMaxCapacity, customizer.TransactionsMaxCollectionCapacity);
            IntListPool = new CrudeObjectPool<List<int>>(() => new List<int>(), onReturn: x => x.Clear(), maxCapacity: BlockSize * 8);
            BlockGroupPool = new CrudeObjectPool<BlockGroup>(() => new BlockGroup(), onReturn: x => x.InPool(), maxCapacity: MIN_CACHE_BLOCK_SIZE);
            FileStreamPool = new CrudeObjectPool<SimFileStream>(() => new SimFileStream(), onReturn: x => x.InPool(), maxCapacity: MIN_CACHE_FILE_SIZE);
            FileSharingDataPool = new CrudeObjectPool<FileSharingData>(()=> new FileSharingData(), onReturn: x=> x.InPool(), maxCapacity: MIN_CACHE_FILE_SIZE);
            DirectoryPool = new CrudeObjectPool<SimDirectory>(() => new SimDirectory(), onReturn: x => x.InPool(), maxCapacity: MIN_CACHE_FILE_SIZE);
            BlockPointersPool = new CrudeObjectPool<BlockPointerData[]>(() => new BlockPointerData[BlockPointersCount], onReturn: InodeData.OnBlockPointerInPool, maxCapacity: BlockSize * FSHeadData.GetPointersSize((uint)BlockSize));
            AttributesPool = new CrudeObjectPool<byte[]>(() => new byte[AttributeSize], onReturn: InodeData.OnAttributesInPool, maxCapacity: BlockSize * 8);
            TransactionPool = new CrudeObjectPool<Transaction>(() => throw new SimFSException(ExceptionType.InternalError, "Cannot Create Transaction here"), maxCapacity: customizer.TransactionsMaxCapacity);
        }

        private int _maxBufferSize;
        internal int MaxBufferSize
        {
            get
            {
                if (_maxBufferSize <= 0)
                    return BlockSize * 8;
                return _maxBufferSize;
            }
            set
            {
                if (value <= 0)
                    _maxBufferSize = value;
                else if (value < MIN_BUFFER_SIZE)
                    _maxBufferSize = MIN_BUFFER_SIZE;
                else
                    _maxBufferSize = value;
            }
        }

        internal int BlockSize { get; private set; }
        internal int BlockPointersCount { get; set; }
        internal int AttributeSize { get; set; }
        internal IObjectPool<List<int>> IntListPool { get; private set; }
        internal IObjectPool<BlockGroup> BlockGroupPool { get; private set; }
        internal IObjectPool<SimFileStream> FileStreamPool { get; private set; }
        internal IObjectPool<FileSharingData> FileSharingDataPool { get; private set; }
        internal IObjectPool<SimDirectory> DirectoryPool { get; private set; }
        internal IObjectPool<BlockPointerData[]> BlockPointersPool { get; private set; }
        internal IObjectPool<byte[]> AttributesPool { get; private set; }
        internal IObjectPool<Transaction> TransactionPool { get; private set; }

        internal TransactionPooling TransactionPooling { get; private set; }

        internal void UpdateBlockSize(int blockSize)
        {
            BlockSize = blockSize;
            IntListPool.MaxCapacity = BlockSize * 8;
            BlockPointersPool.MaxCapacity = BlockSize * FSHeadData.GetPointersSize((uint)BlockSize);
            AttributesPool.MaxCapacity = BlockSize * 8;
            //BufferPool.MaxCapacity = BlockSize / 16;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal BufferHolder<byte> RentBuffer(out Span<byte> span, int minSize = 0, bool exactSize = false)
        {
            if (minSize > MaxBufferSize || minSize <= 0)
                minSize = MaxBufferSize;
            var holder = new BufferHolder<byte>(minSize, exactSize);
            span = holder.Span;
            return holder;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal BufferHolder<char> RentStringBuffer(out Span<char> span)
        {
            var holder = new BufferHolder<char>(256, true);
            span = holder.Span;
            return holder;
        }
    }
}
