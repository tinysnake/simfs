using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SimFS
{
    internal class BlockGroup : IDisposable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long GetBlockGroupLocation(int bgIndex, ushort blockSize, byte inodeSize)
        {
            if (bgIndex < 0)
                throw new IndexOutOfRangeException(nameof(bgIndex));
            return FSHeadData.RESERVED_SIZE +
                bgIndex * (BlockGroupHead.RESERVED_SIZE + 2 * blockSize) +
                8L * bgIndex * (inodeSize * blockSize + blockSize * blockSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long GetBlockLocation(int blockGlobalIndex, ushort blockSize, byte inodeSize)
        {
            if (blockSize <= 0)
                throw new ArgumentException(nameof(blockSize));
            if (blockGlobalIndex < 0)
                throw new IndexOutOfRangeException(nameof(blockGlobalIndex));

            var (bgIndex, blockIndex) = FSMan.GetLocalIndex(blockGlobalIndex, blockSize);

            return GetBlockLocation(bgIndex, blockIndex, blockSize, inodeSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long GetBlockLocation(int bgIndex, int blockIndex, ushort blockSize, byte inodeSize)
        {
            if (blockSize <= 0)
                throw new ArgumentException(nameof(blockSize));
            if (bgIndex < 0)
                throw new IndexOutOfRangeException(nameof(bgIndex));
            if (blockIndex < 0 || blockIndex >= blockSize * 8)
                throw new IndexOutOfRangeException(nameof(blockIndex));

            return GetBlockGroupLocation(bgIndex, blockSize, inodeSize) +
                BlockGroupHead.RESERVED_SIZE + 2 * blockSize +
                blockIndex * blockSize +
                8 * inodeSize * blockSize;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long GetInodeLocation(int bgIndex, ushort blockSize, byte inodeSize, int inodeIndex)
        {
            if (blockSize <= 0)
                throw new ArgumentException(nameof(blockSize));
            if (bgIndex < 0)
                throw new IndexOutOfRangeException(nameof(bgIndex));

            return GetBlockGroupLocation(bgIndex, blockSize, inodeSize) +
                BlockGroupHead.RESERVED_SIZE + 2 * blockSize +
                inodeIndex * inodeSize;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetInodeTableItemsCount(ushort blockSize) => 8 * blockSize;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ThrowsIfNotValid()
        {
            if (GroupIndex < 0 || _fsMan == null)
                throw new SimFSException(ExceptionType.InvalidBlockGroup);
        }

        public void Initialize(FSMan fsMan, int groupIndex, ushort blockSize, byte inodeSize)
        {
            _fsMan = fsMan ?? throw new ArgumentNullException(nameof(fsMan));
            _blockSize = blockSize;
            _inodeSize = inodeSize;
            GroupIndex = groupIndex;
            if (BlockBitmap == null)
                BlockBitmap = new Bitmap(_blockSize, _fsMan.Pooling.IntListPool);
            else
                BlockBitmap.Clear();
            if (InodeBitmap == null)
                InodeBitmap = new Bitmap(_blockSize, _fsMan.Pooling.IntListPool);
            else
                InodeBitmap.Clear();
            _inodeTable ??= new InodeData[GetInodeTableItemsCount(_blockSize)];
        }

        public void Load(FSMan fsMan, int groupIndex, BlockGroupHead head, ReadOnlySpan<byte> blockBitmap, ReadOnlySpan<byte> inodeBitmap, ReadOnlySpan<InodeData> inodeData, byte inodeSize)
        {
            if (_blockSize > 0 && _blockSize != blockBitmap.Length)
                throw new SimFSException(ExceptionType.InvalidBlockGroup, "trying to load blockgroup info to an instance with a different blockSize");

            _fsMan = fsMan ?? throw new ArgumentNullException(nameof(fsMan));
            _blockSize = (ushort)blockBitmap.Length;
            _inodeSize = inodeSize;
            GroupIndex = groupIndex;
            if (BlockBitmap == null)
                BlockBitmap = new Bitmap(blockBitmap, _fsMan.Pooling.IntListPool);
            else
                BlockBitmap.ReInitialize(blockBitmap, _fsMan.Pooling.IntListPool);
            var freeBlocks = BlockBitmap.FreeBits;
            if (InodeBitmap == null)
                InodeBitmap = new Bitmap(inodeBitmap, _fsMan.Pooling.IntListPool);
            else
                InodeBitmap.ReInitialize(inodeBitmap, _fsMan.Pooling.IntListPool);
            var freeInodes = InodeBitmap.FreeBits;
            _inodeTable ??= new InodeData[GetInodeTableItemsCount(_blockSize)];
            inodeData.CopyTo(_inodeTable);

            if (head.freeBlocks != freeBlocks || head.freeInodes != freeInodes)
            {
                throw new SimFSException(ExceptionType.InconsistantDataValue, $"head.freeBlocks: {head.freeBlocks}, calculatedFreeBlocks: {freeBlocks}, " +
                    $"head.freeInodes: {head.freeInodes}, calculatedFreeInodes: {freeInodes}");
            }
        }

        internal BlockGroup()
        {
            InPool();
        }

        internal void InPool()
        {
            GroupIndex = -1;
            _fsMan = null;
        }

        private ushort _blockSize;
        private byte _inodeSize;
        private InodeData[] _inodeTable;
        private FSMan _fsMan;

        public BlockGroupHead Head => new((ushort)BlockBitmap.FreeBits, (ushort)InodeBitmap.FreeBits);
        public int FreeBlocksCount => BlockBitmap.FreeBits;
        public int FreeInodesCount => InodeBitmap.FreeBits;
        public Bitmap BlockBitmap { get; private set; }
        public Bitmap InodeBitmap { get; private set; }

        public int GroupIndex { get; private set; }

        public InodeInfo GetInode(int inodeLocalIndex)
        {
            if (!InodeBitmap.Check(inodeLocalIndex))
                throw new SimFSException(ExceptionType.NotAllocated, $"in bg:{GroupIndex}, the inode at: {inodeLocalIndex}");
            var data = _inodeTable[inodeLocalIndex];
            var globalIndex = FSMan.GetGlobalIndex(GroupIndex, inodeLocalIndex, _blockSize);
            return new InodeInfo(globalIndex, data);
        }

        public InodeInfo AllocateInode(Transaction transaction, InodeUsage usage, int attrSize)
        {
            if (InodeBitmap.FreeBits < 1)
                throw new SimFSException(ExceptionType.NotEnoughBits, "not enough space to allocate inode");
            SelfBeforeChange(transaction);
            var inodeIndex = InodeBitmap.Allocate(1);
            //SimLog.Log("alloc inode: " + FSMan.GetGlobalIndex(GroupIndex, inodeIndex, _blockSize));
            var globalIndex = FSMan.GetGlobalIndex(GroupIndex, inodeIndex, _blockSize);
            var oldInodeData = _inodeTable[inodeIndex];
            InodeData inodeData;
            oldInodeData.ThrowsIfNotEmpty(globalIndex);
            inodeData = InodeData.Create(attrSize, usage, _fsMan.Pooling);
            UpdateInodeInternal(transaction, inodeIndex, inodeData);
            return new InodeInfo(globalIndex, inodeData);
        }

        public void UpdateInode(Transaction transaction, int globalIndex, InodeData inodeData)
        {
            var (bgIndex, inodeIndex) = FSMan.GetLocalIndex(globalIndex, _blockSize);
            if (bgIndex != GroupIndex)
                throw new SimFSException(ExceptionType.BlockGroupNotTheSame, $"you're updating a inode from {bgIndex}, to {GroupIndex}");
            if (!InodeBitmap.Check(inodeIndex))
                throw new SimFSException(ExceptionType.InconsistantDataValue, $"in bg:{GroupIndex}, the inode at: {inodeIndex} is not used, cannot update");
            UpdateInodeInternal(transaction, inodeIndex, inodeData);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UpdateInode(Transaction transaction, InodeInfo inode)
        {
            var (gi, d) = inode;
            UpdateInode(transaction, gi, d);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateInodeInternal(Transaction transaction, int inodeIndex, InodeData inodeData)
        {
            var globalIndex = FSMan.GetGlobalIndex(GroupIndex, inodeIndex, _blockSize);
            transaction.InodeBeforeChange(globalIndex, _inodeTable[inodeIndex]);
            _inodeTable[inodeIndex] = inodeData;
        }

        public void FreeInode(Transaction transaction, int localIndex)
        {
            //SimLog.Log("free inode: " + FSMan.GetGlobalIndex(GroupIndex, localIndex, _blockSize));
            var inodeData = _inodeTable[localIndex];
            inodeData.ThrowsIfNotValid(GroupIndex, localIndex, _blockSize);
            foreach (var bpd in inodeData.blockPointers)
            {
                if (!bpd.IsEmpty)
                    throw new InvalidOperationException("you must free the blocks first");
            }
            SelfBeforeChange(transaction);
            InodeBitmap.Free(localIndex, 1);
            inodeData = inodeData.Free(_fsMan.Pooling);
            UpdateInodeInternal(transaction, localIndex, inodeData);
        }

        public BlockPointerData AllocateBlock(Transaction transaction, int blockCount)
        {
            if (blockCount <= 0)
                blockCount = 1;
            if (blockCount > byte.MaxValue)
                throw new ArgumentOutOfRangeException($"requesting {nameof(blockCount)} is too large than {byte.MaxValue}");

            if (BlockBitmap.FreeBits <= blockCount)
                return default;
            SelfBeforeChange(transaction);
            var blockIndex = BlockBitmap.Allocate(blockCount);
            if (blockIndex < 0)
                return default;
            return new BlockPointerData(FSMan.GetGlobalIndex(GroupIndex, blockIndex, _blockSize), (byte)blockCount);
        }

        public bool ExpandBlockUsage(Transaction transaction, ref BlockPointerData bpd, int blockCount)
        {
            if (blockCount <= 0 || blockCount > byte.MaxValue)
                throw new ArgumentOutOfRangeException($"requesting {nameof(blockCount)} is too large than {byte.MaxValue}");
            if (bpd.blockCount + blockCount > byte.MaxValue)
                return false;

            var (bgIndex, blockIndex) = FSMan.GetLocalIndex(bpd.globalIndex, _blockSize);
            if (bgIndex != GroupIndex)
                throw new SimFSException(ExceptionType.BlockGroupNotTheSame, $"current block pointer is in group: {bgIndex}, but operating group is: {GroupIndex}");

            if (BlockBitmap.FreeBits <= 0)
                return false;

#if DEBUG
            if (!BlockBitmap.RangeCheck(blockIndex, bpd.blockCount, true))
                throw new SimFSException(ExceptionType.InconsistantDataValue);
#endif
            SelfBeforeChange(transaction);
            if (BlockBitmap.ExpandAllocation(blockIndex + bpd.blockCount, blockCount))
            {
                bpd = new BlockPointerData(bpd.globalIndex, (byte)(bpd.blockCount + blockCount));
                return true;
            }
            return false;
        }

        public int ExpandBlockUsageAtBest(Transaction transaction, ref BlockPointerData bpd, int maxBlocksCount)
        {
            if (maxBlocksCount < 0)
                throw new ArgumentOutOfRangeException(nameof(maxBlocksCount));
            if ((bpd.blockCount + maxBlocksCount) > byte.MaxValue)
                throw new ArgumentOutOfRangeException($"requesting {nameof(maxBlocksCount)} + bpd.blockCount: {bpd.blockCount} is larger than {byte.MaxValue}");

            var (bgIndex, blockIndex) = FSMan.GetLocalIndex(bpd.globalIndex, _blockSize);
            if (bgIndex != GroupIndex)
                throw new SimFSException(ExceptionType.BlockGroupNotTheSame, $"current block pointer is in group: {bgIndex}, but operating group is: {GroupIndex}");

            if (BlockBitmap.FreeBits <= 0)
                return 0;

#if DEBUG
            if (!BlockBitmap.RangeCheck(blockIndex, bpd.blockCount, true))
                throw new SimFSException(ExceptionType.InconsistantDataValue);
#endif
            SelfBeforeChange(transaction);
            var allocatedBlocks = BlockBitmap.ExpandAllocationAtBest(blockIndex + bpd.blockCount, maxBlocksCount);
            if (allocatedBlocks > 0)
                bpd = new BlockPointerData(bpd.globalIndex, (byte)(bpd.blockCount + allocatedBlocks));
            return allocatedBlocks;
        }

        public void FreeBlocks(Transaction transaction, BlockPointerData bpd)
        {
            var (bgIndex, blockIndex) = FSMan.GetLocalIndex(bpd.globalIndex, _blockSize);
            if (bgIndex != GroupIndex)
                throw new SimFSException(ExceptionType.BlockGroupNotTheSame, $"current blockGroup: {GroupIndex}, argument block group: {bgIndex}");
            SelfBeforeChange(transaction);
            BlockBitmap.Free(blockIndex, bpd.blockCount);
        }

        public void WriteRawData(int blockIndex, int offset, ReadOnlySpan<byte> data)
        {
            if (offset >= _blockSize)
                throw new SimFSException(ExceptionType.InvalidBlockOffset, $"Offset: {offset}, BlockSize: {_blockSize}");
            var totalBlockCount = SimUtil.Number.IntDivideCeil(offset + data.Length, _blockSize);
            if (blockIndex < 0)
                throw new SimFSException(ExceptionType.BlockIndexOutOfRange, blockIndex.ToString());
            if (blockIndex + totalBlockCount >= BlockBitmap.Size)
                throw new SimFSException(ExceptionType.BlockIndexOutOfRange, $"blockIndex:{blockIndex} and its content blockCount: {totalBlockCount} will out of the block group's boundary");
#if DEBUG
            if (!BlockBitmap.RangeCheck(blockIndex, totalBlockCount, true))
                throw new SimFSException(ExceptionType.NotAllocated, $"blockgroup: {GroupIndex}, blockIndex: {blockIndex}, check length: {totalBlockCount}");
#endif
            var location = GetBlockLocation(GroupIndex, blockIndex, _blockSize, _inodeSize) + offset;
            _fsMan.WriteRawData(location, data);
        }

        public int ReadContentByGlobalIndex(int blockGlobalIndex, int offset, Span<byte> buffer)
        {
            var (bgIndex, blockIndex) = FSMan.GetLocalIndex(blockGlobalIndex, _blockSize);
            if (bgIndex != GroupIndex)
                throw new SimFSException(ExceptionType.BlockGroupNotTheSame, $"current blockGroup: {GroupIndex}, argument block group: {bgIndex}");
            return ReadContent(blockIndex, offset, buffer);
        }

        public int ReadContent(int blockIndex, int offset, Span<byte> buffer)
        {
            var totalBlockCount = SimUtil.Number.IntDivideCeil(offset + buffer.Length, _blockSize);
            if (blockIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(blockIndex));
            if (blockIndex + totalBlockCount >= BlockBitmap.Size)
                throw new ArgumentOutOfRangeException($"blockIndex:{blockIndex} and its content blockCount: {totalBlockCount} will out of the block group's boundary");
#if DEBUG
            if (!BlockBitmap.RangeCheck(blockIndex, totalBlockCount, true))
                throw new SimFSException(ExceptionType.NotAllocated, $"blockgroup: {GroupIndex}, blockIndex: {blockIndex}, check length: {totalBlockCount}");
#endif
            var location = GetBlockLocation(GroupIndex, blockIndex, _blockSize, _inodeSize) + offset;
            return _fsMan.ReadRawData(location, buffer);
        }

        private void SelfBeforeChange(Transaction transaction)
        {
            transaction.BlockGroupBeforeChange(GroupIndex, InodeBitmap.GetBytes().AsSpan(), BlockBitmap.GetBytes().AsSpan());
        }

        public void SaveInodeChanges(Dictionary<int, InodeData> inodeChanges)
        {
            if (inodeChanges == null || inodeChanges.Count == 0)
                return;
            foreach (var (inodeIndex, _) in inodeChanges)
            {
                //SimLog.Log("apply inode: " + FSMan.GetGlobalIndex(GroupIndex, inodeIndex, _blockSize));
                _fsMan.WriteInode(GroupIndex, inodeIndex, _inodeTable[inodeIndex]);
            }
        }

        public void RevertInodeChanges(Dictionary<int, InodeData> inodeChanges)
        {
            if (inodeChanges == null || inodeChanges.Count == 0)
                return;
            foreach (var (inodeIndex, inodeData) in inodeChanges)
            {
                //SimLog.Log("revert inode: " + FSMan.GetGlobalIndex(GroupIndex, inodeIndex, _blockSize));
                _inodeTable[inodeIndex] = inodeData;
            }
        }

        public void SaveMetaChanges()
        {
            _fsMan.UpdateBlockGroupMeta(this);
        }

        public void RevertMetaChanges(ReadOnlySpan<byte> blockBitmap, ReadOnlySpan<byte> inodeBitmap)
        {
            if (_blockSize > 0 && _blockSize != blockBitmap.Length)
                throw new SimFSException(ExceptionType.InvalidBlockGroup, "trying to load blockgroup info to an instance with a different blockSize");

            if (BlockBitmap == null)
                BlockBitmap = new Bitmap(blockBitmap, _fsMan.Pooling.IntListPool);
            else
                BlockBitmap.ReInitialize(blockBitmap, _fsMan.Pooling.IntListPool);
            if (InodeBitmap == null)
                InodeBitmap = new Bitmap(inodeBitmap, _fsMan.Pooling.IntListPool);
            else
                InodeBitmap.ReInitialize(inodeBitmap, _fsMan.Pooling.IntListPool);
        }

        public void Dispose()
        {
            this.ThrowsIfNotValid();
            _fsMan.Pooling.BlockGroupPool.Return(this);
        }
    }
}