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
        private readonly HashSet<int> _openedFiles;
        private int _cachedBlockGroupFirstIndex;

        private int _maxCachedGroupHeadCount = 12800;
        private int _maxCachedDirectories = 1000;

        internal int loadedDirectories = 0;

        public Pooling Pooling { get; private set; }

        public FSMan(string fsFilePath, ushort blockSize, byte attributeSize, ushort bufferSize)
        {
            if (string.IsNullOrEmpty(fsFilePath))
                throw new ArgumentNullException(nameof(fsFilePath));
            var fileExists = File.Exists(fsFilePath);
            _rwLock = new ReadWriteLock();
            _fs = File.Open(fsFilePath, FileMode.OpenOrCreate);
            _loadedBlockGroups = new Dictionary<int, BlockGroup>();
            _cachedBlockGroupHeads = new List<BlockGroupHead>();
            _openedFiles = new HashSet<int>();
            Pooling = new Pooling();
            try
            {
                if (!fileExists)
                {
                    var headData = new FSHeadData(blockSize, attributeSize);
                    _head = SetupFileSystem(headData, bufferSize);
                    var bg0 = AllocateBlockGroup();
                    var rootDirInode = AllocateInode(InodeUsage.Directory, out _, 4);
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
                dir.LoadInfo(this, null, inodeInfo, bg, SimDirectory.ROOT_DIR_NAME.AsMemory());
                return dir;
            }
        }

        public FSHead Head => _head;
        public SimDirectory RootDirectory => _rootDirectory;

        public int MaxCachedGroupHeadCount
        {
            get => _maxCachedGroupHeadCount;
            set
            {
                if (value < 100)
                    value = 100;
                _maxCachedGroupHeadCount = value;
            }
        }

        public int MaxCachedDirectories
        {
            get => _maxCachedDirectories;
            set
            {
                if (value < 100)
                    value = 100;
                _maxCachedDirectories = value;
            }
        }

        public BlockGroup GetBlockGroup(int groupIndex)
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

            WriteBuffer.Flush();
            if (groupIndex < _cachedBlockGroupFirstIndex || groupIndex >= _cachedBlockGroupFirstIndex + _maxCachedGroupHeadCount)
            {
                TryMoveCacheBlockGroupHeadIndex(groupIndex);
            }

            var headIndex = groupIndex - _cachedBlockGroupFirstIndex;
            if (headIndex < 0 || headIndex >= _maxCachedGroupHeadCount)
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
            var stepHigh = stepLow + _maxCachedGroupHeadCount / BG_HEAD_CACHE_STEP;
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

        public InodeInfo GetInode(int inodeGlobalIndex, out BlockGroup bg)
        {
            var (bgIndex, inodeIndex) = GetLocalIndex(inodeGlobalIndex, _head.BlockSize);
            bg = GetBlockGroup(bgIndex);
            return bg.GetInode(inodeIndex);
        }

        public InodeInfo AllocateInode(InodeUsage usage, out BlockGroup bg, int blockCount = -1)
            => AllocateInode(usage, out bg, -1, blockCount);

        private InodeInfo AllocateInode(InodeUsage usage, out BlockGroup bg, int exceptIndex, int blockCount = -1)
        {
            if (blockCount <= 0)
                blockCount = 1;
            BlockPointerData bpd;
            bpd = TryAllocateSpace(1, blockCount, exceptIndex, out bg);
            if (!bpd.IsEmpty)
            {
                var result = bg.AllocateInode(usage, _head.AttributeSize);
                AssignBlockToInode(bg, ref result, bpd);
                return result;
            }
            throw new InvalidOperationException();
        }

        public InodeInfo AllocateInodeNear(int inodeGlobalIndex, InodeUsage usage, out BlockGroup bg, int blockCount = -1)
        {
            if (blockCount <= 0)
                blockCount = 1;
            var (bgIndex, _) = GetLocalIndex(inodeGlobalIndex, _head.BlockSize);
            bg = GetBlockGroup(bgIndex);
            if (bg.FreeInodesCount > 0 && bg.FreeBlocksCount >= blockCount)
            {
                var bpd = bg.AllocateBlock(blockCount);
                if (!bpd.IsEmpty)
                {
                    var result = bg.AllocateInode(usage, _head.AttributeSize);
                    AssignBlockToInode(bg, ref result, bpd);
                    return result;
                }
            }
            return AllocateInode(usage, out bg, blockCount);
        }

        private void AssignBlockToInode(BlockGroup bg, ref InodeInfo info, BlockPointerData bpd)
        {
            var data = info.data;
            if (!data.blockPointers[0].IsEmpty)
                throw new SimFSException(ExceptionType.InternalError, "this inode is not empty");
            data.blockPointers[0] = bpd;
            bg.UpdateInode(info);
        }

        public void FreeInode(int inodeGlobalIndex)
        {
            var (bgIndex, localIndex) = GetLocalIndex(inodeGlobalIndex, _head.BlockSize);
            var bg = GetBlockGroup(bgIndex);
            var inodeInfo = bg.GetInode(localIndex);
            FreeInodeBlocks(inodeInfo.data);
            bg.FreeInode(localIndex);
        }

        public void FreeInode(InodeInfo inode)
        {
            var (bgIndex, localIndex) = GetLocalIndex(inode.globalIndex, _head.BlockSize);
            var inodeBg = GetBlockGroup(bgIndex);
            FreeInodeBlocks(inode.data);
            inodeBg.FreeInode(localIndex);
        }

        private void FreeInodeBlocks(InodeData data)
        {
            for (var i = 0; i < data.blockPointers.Length; i++)
            {
                var bpd = data.blockPointers[i];
                if (bpd.IsEmpty)
                    break;
                var (bgIndex, _) = GetLocalIndex(bpd.globalIndex, _head.BlockSize);
                var bg = GetBlockGroup(bgIndex);
                bg.FreeBlocks(bpd);
                data.blockPointers[i] = default;
            }
        }

        public BlockPointerData AllocateBlockNearInode(int inodeGlobalIndex, out BlockGroup bg, int blockCount = -1)
        {
            if (blockCount <= 0)
                blockCount = 1;
            var (bgIndex, _) = GetLocalIndex(inodeGlobalIndex, _head.BlockSize);
            bg = GetBlockGroup(bgIndex);
            if (bg.FreeBlocksCount >= blockCount)
            {
                var blockPointer = bg.AllocateBlock(blockCount);
                if (!blockPointer.IsEmpty)
                    return blockPointer;
            }
            var nbps = TryAllocateSpace(0, blockCount, inodeGlobalIndex, out bg);
            if (nbps.IsEmpty)
                throw new NotImplementedException("allocate from unloaded groups is not implemented");
            return nbps;
        }

        private BlockPointerData TryAllocateSpace(int inodeCountRequired, int blocCountRequired, int exceptIndex, out BlockGroup bg)
        {
            BlockPointerData bpd;
            foreach (var (index, lbg) in _loadedBlockGroups)
            {
                if (index == exceptIndex)
                    continue;
                if (lbg.FreeInodesCount >= inodeCountRequired && lbg.FreeBlocksCount >= blocCountRequired)
                {
                    bg = lbg;
                    bpd = lbg.AllocateBlock(blocCountRequired);
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
                    bpd = bg.AllocateBlock(blocCountRequired);
                    if (!bpd.IsEmpty)
                        return bpd;
                }
            }

            bg = AllocateBlockGroup();
            bpd = bg.AllocateBlock(blocCountRequired);
            if (!bpd.IsEmpty)
                return bpd;
            throw new SimFSException(ExceptionType.InvalidOperation, "There're no demands that a brand new BlockGroup cannot fulfill!");
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SimFileStream LoadFileStream(InodeInfo inode, BlockGroup bg = null)
        {
            var fs = Pooling.FileStreamPool.Get();
            fs.LoadFileStream(this, inode, bg);
            return fs;
        }

        public void TryOpenFile(int inodeGlobalIndex)
        {
            if (_openedFiles.Contains(inodeGlobalIndex))
                throw new SimFSException(ExceptionType.FileAlreadyOpended, $"inode: {inodeGlobalIndex}");

            _openedFiles.Add(inodeGlobalIndex);
        }

        public void CloseFile(int inodeGlobalIndex)
        {
            _openedFiles.Remove(inodeGlobalIndex);
        }

        public void Dispose()
        {
            _rootDirectory.Dispose();
            foreach (var (_, bg) in _loadedBlockGroups)
            {
                bg.Dispose();
            }
            _loadedBlockGroups.Clear();
            DisposeIO();
        }
    }
}
