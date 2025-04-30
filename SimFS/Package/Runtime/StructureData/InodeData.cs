using System;
using System.Runtime.CompilerServices;

namespace SimFS
{
    internal readonly struct InodeData
    {
        public static InodeData Empty => new();
        internal static void OnBlockPointerInPool(BlockPointerData[] arr)
        {
            arr.AsSpan().Clear();
        }

        internal static void OnAttributesInPool(byte[] obj)
        {
            obj.AsSpan().Clear();
        }

        public static InodeData Create(int attributeSize, InodeUsage typeInfo, Pooling pool)
        {
            var pointers = pool.BlockPointersPool.Get();
            var attrs = attributeSize <= 0 ? Array.Empty<byte>() : pool.AttributesPool.Get();
            return new InodeData(pointers, attrs, typeInfo);
        }

        public static int GetInodeSize(int maxBlocksCount, int attributeSize)
        {
            return sizeof(int) + sizeof(InodeUsage) + maxBlocksCount * BlockPointerData.MemSize + attributeSize;
        }

        private InodeData(BlockPointerData[] blockPointers, byte[] attributes, InodeUsage usage)
        {
            this.blockPointers = blockPointers;
            this.attributes = attributes;
            length = 0;
            this.usage = usage;
        }

        public InodeData(int length, InodeUsage usage, byte[] attr, BlockPointerData[] blockPointers)
        {
            this.length = length;
            this.usage = usage;
            attributes = attr;
            this.blockPointers = blockPointers;
        }

        public readonly int length;
        public readonly InodeUsage usage;
        public readonly byte[] attributes;
        public readonly BlockPointerData[] blockPointers;

        public bool IsEmpty => usage == InodeUsage.Unused;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ThrowsIfNotValid(int index)
        {
            if (IsEmpty)
                throw new SimFSException(ExceptionType.InvalidInode, "current inode is empty, index:" + index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ThrowsIfNotValid(int groupIndex, int index, ushort blockSize)
        {
            if (IsEmpty)
                throw new SimFSException(ExceptionType.InvalidInode, "current inode is empty, index:" + +FSMan.GetGlobalIndex(groupIndex, index, blockSize));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ThrowsIfNotEmpty(int index)
        {
            if (length != 0 && usage != InodeUsage.Unused)
                throw new SimFSException(ExceptionType.InvalidInode, "required inode is not empty, index:" + index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ThrowsIfNotEmpty(int groupIndex, int index, ushort blockSize)
        {
            if (length != 0 && usage != InodeUsage.Unused)
                throw new SimFSException(ExceptionType.InvalidInode, "required inode is not empty, index:" + FSMan.GetGlobalIndex(groupIndex, index, blockSize));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public InodeData Free(Pooling pool)
        {
            if (blockPointers != null)
                pool.BlockPointersPool.Return(blockPointers);
            if (attributes != null && attributes.Length > 0)
                pool.AttributesPool.Return(attributes);
            return new InodeData(0, InodeUsage.Unused, null, null);
        }
    }
}
