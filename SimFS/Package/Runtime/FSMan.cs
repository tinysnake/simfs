using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace SimFS
{
    internal partial class FSMan : IDisposable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetGlobalIndex(int groupIndex, int blockIndex, ushort blockSize)
        {
            return groupIndex * 8 * blockSize + blockIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (int bgIndex, int blockIndex) GetLocalIndex(int blockGlobalIndex, ushort blockSize)
        {
            if (blockGlobalIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(blockGlobalIndex));

            var bgIndex = blockGlobalIndex / (8 * blockSize);
            var blockIndex = blockGlobalIndex % (8 * blockSize);
            return (bgIndex, blockIndex);
        }

        private const int BG_HEAD_CACHE_STEP = 50;

        private readonly FSHead _head;
        private readonly SimDirectory _rootDirectory;
        private readonly Dictionary<int, BlockGroup> _loadedBlockGroups;
        private readonly List<BlockGroupHead> _cachedBlockGroupHeads;
        private readonly HashSet<Transaction> _allocatedTransactions;
        private readonly Dictionary<int, SimFileStream> _opendedFiles;
        private int _cachedBlockGroupFirstIndex;

        internal int loadedDirectories = 0;

        internal Pooling Pooling { get; private set; }
        internal Customizer Customizer { get; private set; }

        internal FSMan(Stream stream, ushort blockSize, byte attributeSize, Customizer customizer = null)
        {
            if (!stream.CanSeek || !stream.CanRead || !stream.CanWrite)
                throw new ArgumentException("stream is not valid");
            _fs = stream;
            _loadedBlockGroups = new Dictionary<int, BlockGroup>();
            _cachedBlockGroupHeads = new List<BlockGroupHead>();
            _allocatedTransactions = new HashSet<Transaction>();
            _opendedFiles = new Dictionary<int, SimFileStream>();
            _smallBuffer = new byte[FSHeadData.RESERVED_SIZE];
            Customizer = customizer ?? new Customizer();
            Pooling = new Pooling(Customizer);
            try
            {
                if (stream.Length == 0)
                {
                    var headData = new FSHeadData(blockSize, attributeSize);
                    _head = SetupFileSystem(headData);
                    var bg0 = AllocateBlockGroup();
                    using var t = BeginTransaction(TransactionMode.Immediate, null);
                    var rootDirInode = AllocateInode(t, InodeUsage.Directory, out _, 4);
                    _rootDirectory = LoadDirectory(rootDirInode, bg0);
                }
                else
                {
                    _head = InitializeFileSystem();
                    var bg0 = GetBlockGroup(0);
                    var rootDirInode = bg0.GetInode(0);
                    _rootDirectory = LoadDirectory(rootDirInode, bg0);
                }
            }
            catch
            {
                _fs.Close();
                throw;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            SimDirectory LoadDirectory(InodeInfo inodeInfo, BlockGroup bg)
            {
                var dir = Pooling.DirectoryPool.Get();
                dir.LoadInfo(this, null, inodeInfo, bg, SimDirectory.ROOT_DIR_NAME.AsMemory(), 0);
                return dir;
            }

        }

        internal FSHead Head => _head;
        internal SimDirectory RootDirectory => _rootDirectory;

        internal Transaction BeginTransaction(TransactionMode mode, string friendlyName)
        {
            Transaction t;
            if (Pooling.TransactionPool.HasItem)
                t = Pooling.TransactionPool.Get();
            else
                t = new Transaction();
            t.ReInitialize(this, mode, friendlyName);
            _allocatedTransactions.Add(t);
            return t;
        }

        internal void EndTransaction(Transaction t)
        {
            _allocatedTransactions.Remove(t);
            Pooling.TransactionPool.Return(t);
        }

        internal BlockGroup GetBlockGroup(int groupIndex)
        {
            if (_loadedBlockGroups.TryGetValue(groupIndex, out var bg))
                return bg;
            if ((uint)groupIndex >= (uint)_head.BlockGroupCount)
                throw new SimFSException(ExceptionType.InvalidBlockGroup, $"reading non existing blockgroup at index: {groupIndex}");
            bg = Pooling.BlockGroupPool.Get();
            ReadBlockGroup(bg, groupIndex);
            AddBlockGroupHead(bg);
            _loadedBlockGroups.Add(bg.GroupIndex, bg);
            return bg;
        }

        private BlockGroup AllocateBlockGroup()
        {
            var index = _head.BlockGroupCount;
            var bg = Pooling.BlockGroupPool.Get();
            InitializeBlockGroup(bg, index, false);
            AddBlockGroupHead(bg);
            _loadedBlockGroups.Add(index, bg);
            return bg;
        }

        private void AddBlockGroupHead(BlockGroup bg)
        {
            var groupIndex = bg.GroupIndex;
            if (_cachedBlockGroupFirstIndex + _cachedBlockGroupHeads.Count <= groupIndex)
            {
                var count = groupIndex - _cachedBlockGroupHeads.Count - _cachedBlockGroupFirstIndex;
                for (var i = 0; i <= count; i++)
                {
                    _cachedBlockGroupHeads.Add(default);
                }
            }
            _cachedBlockGroupHeads[groupIndex - _cachedBlockGroupFirstIndex] = bg.Head;
        }

        private BlockGroupHead GetBlockGroupHead(int groupIndex)
        {
            if ((uint)groupIndex >= (uint)_head.BlockGroupCount)
                throw new SimFSException(ExceptionType.InvalidBlockGroup, $"reading non existing blockgroup at index: {groupIndex}");

            if (_loadedBlockGroups.TryGetValue(groupIndex, out var bg))
                return bg.Head;

            if (groupIndex < _cachedBlockGroupFirstIndex || groupIndex >= _cachedBlockGroupFirstIndex + Customizer.MaxCachedBlockGroupHead)
            {
                TryMoveCacheBlockGroupHeadIndex(groupIndex);
            }

            var headIndex = groupIndex - _cachedBlockGroupFirstIndex;
            if (headIndex < 0 || headIndex >= Customizer.MaxCachedBlockGroupHead)
                throw new IndexOutOfRangeException(nameof(headIndex));

            if (headIndex >= _cachedBlockGroupHeads.Count)
            {
                var lastIndex = _cachedBlockGroupHeads.Count;
                var count = headIndex - lastIndex + 1;
                for (var i = 0; i < count; i++)
                {
                    _cachedBlockGroupHeads.Add(ReadBlockGroupHead(lastIndex + i));
                }
            }
            return _cachedBlockGroupHeads[headIndex];
        }

        private void TryMoveCacheBlockGroupHeadIndex(int groupIndex)
        {
            var curStep = groupIndex / BG_HEAD_CACHE_STEP;
            var stepLow = _cachedBlockGroupFirstIndex / BG_HEAD_CACHE_STEP;
            var stepHigh = stepLow + Customizer.MaxCachedBlockGroupHead / BG_HEAD_CACHE_STEP;
            var stepMax = _head.BlockGroupCount / BG_HEAD_CACHE_STEP;
            var stepChange = 0;
            var dir = 0;
            if (curStep < stepLow && curStep >= 0)
            {
                dir = -1;
                stepChange = curStep - stepLow;
            }
            else if (curStep > stepHigh && curStep <= stepMax)
            {
                dir = 1;
                stepChange = curStep - stepLow;
            }
            if (stepChange == 0)
                return;
            var step = dir > 0 ? stepHigh : stepLow;
            var startReadBgIndex = (step + stepChange * dir) * BG_HEAD_CACHE_STEP;
            var size = stepChange * BG_HEAD_CACHE_STEP;
            int insertIndex;
            if (dir < 0)
            {
                _cachedBlockGroupHeads.RemoveRange(_cachedBlockGroupHeads.Count - 1, size);
                _cachedBlockGroupHeads.InsertRange(0, Enumerable.Repeat<BlockGroupHead>(default, size));
                insertIndex = 0;
            }
            else
            {
                _cachedBlockGroupHeads.RemoveRange(0, size);
                insertIndex = _cachedBlockGroupHeads.Count;
                _cachedBlockGroupHeads.InsertRange(insertIndex, Enumerable.Repeat<BlockGroupHead>(default, size));
            }

            for (var i = 0; i < size; i++)
            {
                var readIndex = startReadBgIndex + i;
                if (readIndex >= _head.BlockGroupCount)
                    break;
                else if (readIndex < 0)
                    throw new InvalidOperationException();
                var head = ReadBlockGroupHead(readIndex);
                _cachedBlockGroupHeads[insertIndex + i] = head;
            }

            _cachedBlockGroupFirstIndex += dir * size;
        }

        internal InodeInfo GetInode(int inodeGlobalIndex, out BlockGroup bg)
        {
            var (bgIndex, inodeIndex) = GetLocalIndex(inodeGlobalIndex, _head.BlockSize);
            bg = GetBlockGroup(bgIndex);
            return bg.GetInode(inodeIndex);
        }

        internal InodeInfo AllocateInode(Transaction transaction, InodeUsage usage, out BlockGroup bg, int blockCount = -1)
            => AllocateInode(transaction, usage, out bg, -1, blockCount);

        private InodeInfo AllocateInode(Transaction transaction, InodeUsage usage, out BlockGroup bg, int exceptIndex, int blockCount = -1)
        {
            if (blockCount <= 0)
                blockCount = 1;
            BlockPointerData bpd;
            bpd = TryAllocateSpace(transaction, 1, blockCount, exceptIndex, out bg);
            if (!bpd.IsEmpty)
            {
                var result = bg.AllocateInode(transaction, usage, _head.AttributeSize);
                AssignBlockToInode(transaction, bg, ref result, bpd);
                return result;
            }
            throw new SimFSException(ExceptionType.UnableToAllocateInode);
        }

        internal InodeInfo AllocateInodeNear(Transaction transaction, int inodeGlobalIndex, InodeUsage usage, out BlockGroup bg, int blockCount = -1)
        {
            if (blockCount <= 0)
                blockCount = 1;
            var (bgIndex, _) = GetLocalIndex(inodeGlobalIndex, _head.BlockSize);
            bg = GetBlockGroup(bgIndex);
            if (bg.FreeInodesCount > 0 && bg.FreeBlocksCount >= blockCount)
            {
                var bpd = bg.AllocateBlock(transaction, blockCount);
                if (!bpd.IsEmpty)
                {
                    var result = bg.AllocateInode(transaction, usage, _head.AttributeSize);
                    AssignBlockToInode(transaction, bg, ref result, bpd);
                    return result;
                }
            }
            return AllocateInode(transaction, usage, out bg, blockCount);
        }

        private void AssignBlockToInode(Transaction transaction, BlockGroup bg, ref InodeInfo info, BlockPointerData bpd)
        {
            var data = info.data;
            if (!data.blockPointers[0].IsEmpty)
                throw new SimFSException(ExceptionType.InvalidInode, $"this inode:{info.globalIndex} is not empty, cannot assign block to it");
            data.blockPointers[0] = bpd;
            bg.UpdateInode(transaction, info);
        }

        internal void FreeInode(Transaction transaction, int inodeGlobalIndex)
        {
            var (bgIndex, localIndex) = GetLocalIndex(inodeGlobalIndex, _head.BlockSize);
            var bg = GetBlockGroup(bgIndex);
            var inodeInfo = bg.GetInode(localIndex);
            FreeInodeBlocks(transaction, inodeInfo.data);
            bg.FreeInode(transaction, localIndex);
        }

        internal void FreeInode(Transaction transaction, InodeInfo inode)
        {
            var (bgIndex, localIndex) = GetLocalIndex(inode.globalIndex, _head.BlockSize);
            var inodeBg = GetBlockGroup(bgIndex);
            FreeInodeBlocks(transaction, inode.data);
            inodeBg.FreeInode(transaction, localIndex);
        }

        private void FreeInodeBlocks(Transaction transaction, InodeData data)
        {
            for (var i = 0; i < data.blockPointers.Length; i++)
            {
                var bpd = data.blockPointers[i];
                if (bpd.IsEmpty)
                    break;
                var (bgIndex, _) = GetLocalIndex(bpd.globalIndex, _head.BlockSize);
                var bg = GetBlockGroup(bgIndex);
                bg.FreeBlocks(transaction, bpd);
                data.blockPointers[i] = default;
            }
        }

        internal BlockPointerData AllocateBlockNearInode(Transaction transaction, int inodeGlobalIndex, out BlockGroup bg, int blockCount = -1)
        {
            if (blockCount <= 0)
                blockCount = 1;
            var (bgIndex, _) = GetLocalIndex(inodeGlobalIndex, _head.BlockSize);
            bg = GetBlockGroup(bgIndex);
            if (bg.FreeBlocksCount >= blockCount)
            {
                var blockPointer = bg.AllocateBlock(transaction, blockCount);
                if (!blockPointer.IsEmpty)
                    return blockPointer;
            }
            var nbps = TryAllocateSpace(transaction, 0, blockCount, inodeGlobalIndex, out bg);
            if (nbps.IsEmpty)
                throw new NotImplementedException("allocate from unloaded groups is not implemented");
            return nbps;
        }

        private BlockPointerData TryAllocateSpace(Transaction transaction, int inodeCountRequired, int blocCountRequired, int exceptIndex, out BlockGroup bg)
        {
            BlockPointerData bpd;
            foreach (var (index, lbg) in _loadedBlockGroups)
            {
                if (index == exceptIndex)
                    continue;
                if (lbg.FreeInodesCount >= inodeCountRequired && lbg.FreeBlocksCount >= blocCountRequired)
                {
                    bg = lbg;
                    bpd = lbg.AllocateBlock(transaction, blocCountRequired);
                    if (!bpd.IsEmpty)
                        return bpd;
                }
            }

            for (var i = 0; i < _head.BlockGroupCount; i++)
            {
                if (i == exceptIndex)
                    continue;
                if (_loadedBlockGroups.ContainsKey(i))
                    continue;
                var head = GetBlockGroupHead(i);
                if (head.freeInodes >= inodeCountRequired && head.freeBlocks >= blocCountRequired)
                {
                    bg = GetBlockGroup(i);
                    bpd = bg.AllocateBlock(transaction, blocCountRequired);
                    if (!bpd.IsEmpty)
                        return bpd;
                }
            }

            bg = AllocateBlockGroup();
            bpd = bg.AllocateBlock(transaction, blocCountRequired);
            if (!bpd.IsEmpty)
                return bpd;
            throw new SimFSException(ExceptionType.InternalError, "There're no demands that a brand new BlockGroup cannot fulfill!");
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal SimFileStream LoadFileStream(InodeInfo inode, Transaction transaction, SimDirectory parentDir, BlockGroup bg = null)
        {
            var fs = Pooling.FileStreamPool.Get();
            try
            {
                TryOpenFile(inode.globalIndex, fs);
                if (parentDir != null)
                    fs.LoadFileStream(this, inode, parentDir, bg);
                else
                    fs.LoadFileStreamWithoutParent(this, inode, bg);
                if (transaction != null)
                    fs.WithTransaction(transaction);
            }
            catch
            {
                Pooling.FileStreamPool.Return(fs);
                throw;
            }
            return fs;
        }

        private void TryOpenFile(int inodeGlobalIndex, SimFileStream file)
        {
            if (_opendedFiles.ContainsKey(inodeGlobalIndex))
                throw new SimFSException(ExceptionType.FileAlreadyOpened);
            _opendedFiles[inodeGlobalIndex] = file;
        }

        internal void CloseFile(int inodeGlobalIndex, SimFileStream file)
        {
            if (!file.IsValid || file.InodeInfo.globalIndex != inodeGlobalIndex)
                throw new SimFSException(ExceptionType.InvalidFileStream);
            _opendedFiles.Remove(inodeGlobalIndex);
        }

        internal bool IsFileOpen(int inodeGlobalIndex) => _opendedFiles.ContainsKey(inodeGlobalIndex);

        internal SimFileStream GetLoadedFileStream(int inodeGlobalIndex)
        {
            if (_opendedFiles.TryGetValue(inodeGlobalIndex, out var fs))
                return fs;
            return null;
        }

        public void ForceDispose()
        {
            DisposeIO();
        }

        public void Dispose()
        {
            _rootDirectory.Dispose();
            foreach (var (_, bg) in _loadedBlockGroups)
            {
                bg.Dispose();
            }

            if (_allocatedTransactions.Count > 0)
            {
                throw new SimFSException(ExceptionType.UnsaveChangesMade);
            }

            _loadedBlockGroups.Clear();
            DisposeIO();
        }
    }
}
