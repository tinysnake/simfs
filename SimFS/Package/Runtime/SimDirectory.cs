using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace SimFS
{
    internal class SimDirectory : IDisposable
    {
        private class UnusedEntryComparer : IComparer<(int, List<int>)>
        {
            public static UnusedEntryComparer Default { get; } = new();
            public int Compare((int, List<int>) x, (int, List<int>) y)
            {
                return x.Item1 - y.Item1;
            }
        }

        internal const string ROOT_DIR_NAME = "^r";
        private static readonly char[] InvalidChars = new[] { '\0', '/' };
        private static readonly List<SimDirectory> _trimTempList = new();

        public static void GetAllChildren(List<ReadOnlyMemory<char>> basePaths, SimDirectory dir, ICollection<ReadOnlyMemory<char>> list, SimFSType type)
        {
            foreach (var child in dir.SubDirectories)
            {
                basePaths.Add(child);
                if ((type & SimFSType.Directory) > 0)
                    list.Add(SimUtil.Path.BuildPath(basePaths, child.Span));
                if ((type & SimFSType.File) > 0)
                    GetAllChildren(basePaths, dir.GetDirectory(child.Span), list, type);
                basePaths.RemoveAt(basePaths.Count - 1);
            }

            if ((type & SimFSType.File) > 0)
            {
                foreach (var fileName in dir.Files)
                {
                    list.Add(SimUtil.Path.BuildPath(basePaths, fileName.Span));
                }
            }
        }

        private static void TrimLoadedDirectories(FSMan fsMan, SimDirectory dir)
        {
            // this is my self-made "algorithm" of trimming the directories, probably performs badly,
            // but "should" smarter than just trimming from the top
            var targetTrimNum = fsMan.Customizer.MaxCachedDirectoires / 10;
            var trimCount = 0;
            // first find out current dir chain
            _trimTempList.Clear();
            var parent = dir;
            do
            {
                _trimTempList.Add(parent);
                parent = parent.Parent;
            }
            while (parent != null);
            _trimTempList.Reverse();
            // start from the middle, why from the middle, 
            //   1. the top directories probably have the fewest folders, and the revisit chances are high.
            //   2. the bottom directoires may have the most folders, but the revisit chances are even higher.
            var maxLoop = 1000;
            var middlePt = 0;
            while (maxLoop-- > 0)
            {
                var loopStart = 0;
                middlePt = loopStart + (_trimTempList.Count - loopStart) / 2;
                var tempMiddleIndex = middlePt;
                var middleLoadedNum = 0;
                // from the middle and step back to top to make sure we have the one that has the most loaded directories.
                // the more loaded directories means the less important the subdirectories are.
                // by this method, we can find out maybe the root directory containes the most loaded directoires.
                for (var i = tempMiddleIndex; i >= 0; i--)
                {
                    var tempDir = _trimTempList[i];
                    var count = tempDir._loadedDirectories.Count;
                    if (count > middleLoadedNum)
                    {
                        middlePt = i;
                        middleLoadedNum = count;
                    }
                }
                // if the dirs from top to middlePt only contains 1 child, it means this directory chain is too long,
                // and this directory tree didn't start to branch out on middlePt,
                // so now we should start from middlePt and test again, until either:
                //   a. _trimTempList.Count - middlePt <= 2
                //   b. middleLoadedNum > 1
                if (middleLoadedNum <= 1)
                {
                    loopStart = middlePt;
                    if (loopStart >= _trimTempList.Count - 1)
                        break;
                }
                else
                    break;
            }
            // 4. now let's walk from the appropriate point to the end and trim all the "hopefully will never revisit again" dirs.
            // why (count - 1)? because the last one is itself.
            var keysList = new List<int>();
            for (; middlePt < _trimTempList.Count - 1; middlePt++)
            {
                var loadedDirs = _trimTempList[middlePt]._loadedDirectories;
                keysList.AddRange(loadedDirs.Select(x => x.Key));
                var childDir = _trimTempList[middlePt + 1];
                foreach (var key in keysList)
                {
                    if (key == childDir.Index)
                        continue;
                    var num = fsMan.loadedDirectories;
                    var dirToRemove = loadedDirs[key];
                    if (!dirToRemove.IsDirty)
                    {
                        loadedDirs[key].Dispose();
                        loadedDirs.Remove(key);
                        trimCount += num - fsMan.loadedDirectories;
                    }
                    if (trimCount >= targetTrimNum)
                        return;
                }
                keysList.Clear();
            }
            // loaded directories may have unsaved changed so this might not trim down enough directories.
            // throw new SimFSException(ExceptionType.InternalError, "unexpected behavior on directory triming");
        }

        internal SimDirectory()
        {

        }

        internal void InPool()
        {
            if (_loadedDirectories.Count > 0)
                throw new SimFSException(ExceptionType.InvalidDirectory, "should first dispose all loaded Directories, then InPool");
            if (_stream != null)
                throw new SimFSException(ExceptionType.InvalidDirectory, "should first dispose self stream, then InPool");
            if (transactionData != null)
            {
                var tp = _fsMan.Pooling.TransactionPooling;
                if (transactionData.Count > 0)
                {
                    Span<int> transactionIds = stackalloc int[transactionData.Count];
                    var tidIndex = 0;
                    foreach (var (id, _) in transactionData)
                    {
                        transactionIds[tidIndex++] = id;
                    }
                    foreach (var tid in transactionIds)
                    {
                        DisposeTransactionData(tid);
                    }
                }
                tp.DirTransDataDictPool.Return(transactionData);
                transactionData = null;
            }
            _inodeGlobalIndex = -1;
            _inode = default;
            _fsMan = null;
            _entries.Clear();
            _names.Clear();
            _files.Clear();
            _dirs.Clear();
            _unusedEntries.Clear();
            _initialized = false;
            _disposed = true;
        }

        internal void LoadInfo(FSMan fsMan, SimDirectory parent, InodeInfo inode, BlockGroup bg, ReadOnlyMemory<char> name, int childEntryIndex)
        {
            var isRootDir = parent == null && name.Span.CompareTo(ROOT_DIR_NAME, StringComparison.Ordinal) == 0;
            if (!isRootDir && parent == null)
                throw new ArgumentNullException(nameof(parent));
            if (!isRootDir)
                ThrowsIfNameIsInvalid(name.Span);
            inode.ThrowsIfNotValid();

            var comparer = fsMan.Customizer.NameComparer;
            _loadedDirectories ??= new Dictionary<int, SimDirectory>();
            _files ??= new Dictionary<ReadOnlyMemory<char>, int>(comparer);
            _dirs ??= new Dictionary<ReadOnlyMemory<char>, int>(comparer);

            _fsMan = fsMan;
            _childIndex = childEntryIndex;
            Parent = parent;
            Name = name;
            (_inodeGlobalIndex, _inode) = inode;
            _stream = _fsMan.Pooling.FileStreamPool.Get();
            //if (parent != null)
            //SimLog.Log($"creating dir: {parent.BuildFullName(name.Span)}, inode: {inode.globalIndex}");
            _stream = _fsMan.DangerouslyLoadFileStream(inode, FileAccess.ReadWrite, null, this, bg);
            _fsMan.loadedDirectories++;
            if (_fsMan.loadedDirectories > _fsMan.Customizer.MaxCachedDirectoires)
                TrimLoadedDirectories(_fsMan, this);
            _disposed = false;
            _deleted = false;
        }

        private int _inodeGlobalIndex;
        private int _childIndex;
        private InodeData _inode;
        private FSMan _fsMan;
        private SimFileStream _stream;
        private bool _initialized;
        private bool _deleted;
        private bool _disposed;

        private Dictionary<int, SimDirectory> _loadedDirectories = new();
        private Dictionary<ReadOnlyMemory<char>, int> _files = new(NameComparer.Ordinal);
        private Dictionary<ReadOnlyMemory<char>, int> _dirs = new(NameComparer.Ordinal);
        private readonly List<DirectoryEntryData> _entries = new();
        private readonly List<ReadOnlyMemory<char>> _names = new();
        private readonly SortedList<(int nameLength, List<int> indices)> _unusedEntries = new(UnusedEntryComparer.Default);
        internal Dictionary<int, DirectoryTransactionData> transactionData = null;

        public InodeInfo InodeInfo => _stream.InodeInfo;
        public int Index => _childIndex;
        public ReadOnlyMemory<char> Name { get; private set; }
        public SimDirectory Parent { get; private set; }

        public bool IsValid => _stream != null;

        public KeyEnumerable Files
        {
            get
            {
                TryInitialize();
                return new KeyEnumerable(_files);
            }
        }
        public KeyEnumerable SubDirectories
        {
            get
            {
                TryInitialize();
                return new KeyEnumerable(_dirs);
            }
        }

        public AllEntryEnumerable Children
        {
            get
            {
                TryInitialize();
                return new AllEntryEnumerable(this);
            }
        }

        private bool IsDirty => transactionData != null && transactionData.Any(static x =>
            (x.Value.childrenChanges?.Count ?? 0) + (x.Value.entryChanges?.Count ?? 0) > 0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private BufferHolder<char> TempName(ReadOnlySpan<char> dirName, out ReadOnlyMemory<char> tempName)
        {
            if (dirName.Length > 255)
                throw new ArgumentOutOfRangeException(nameof(dirName) + "is too long");
            var bufferHolder = _fsMan.Pooling.RentStringBuffer(out var span);
            dirName.CopyTo(span);
            tempName = bufferHolder.Memory[..dirName.Length];
            return bufferHolder;
        }

        public void GetFullNameSegments(List<ReadOnlyMemory<char>> pathHolder)
        {
            var dir = this;
            do
            {
                if (dir.Parent == null && dir.Name.Span.CompareTo(ROOT_DIR_NAME, StringComparison.Ordinal) == 0)
                    break;
                pathHolder.Add(dir.Name);
                dir = dir.Parent;
            } while (dir != null);
            pathHolder.Reverse();
        }

        public ReadOnlyMemory<char> BuildFullName(ReadOnlySpan<char> childName = default)
        {
            var list = SimUtil.Path.PathSegmentsHolder;
            GetFullNameSegments(list);
            return SimUtil.Path.BuildPath(list, childName);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ThrowsIfNotValid() => ThrowsIfNotValid(true, true);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowsIfNotValid(bool checkDisposed, bool checkDeleted)
        {
            if (checkDisposed && _disposed)
                throw new SimFSException(ExceptionType.DirectoryAlreadyDisposed);
            if (checkDeleted)
            {
                if (_deleted)
                    throw new SimFSException(ExceptionType.DirectoryAlreadyDeleted);
                if (_inodeGlobalIndex < 0 || _fsMan == null || _inode.IsEmpty || _stream == null)
                    throw new SimFSException(ExceptionType.InvalidDirectory);
                if (_stream != null && _stream.InodeInfo.globalIndex != _inodeGlobalIndex)
                    throw new SimFSException(ExceptionType.InvalidDirectory, $"inode index doesn't match, dir: {_inodeGlobalIndex}, stream: {_stream.InodeInfo.globalIndex}");
            }
            else if (_deleted)
            {
                if (_inodeGlobalIndex < 0 || _fsMan == null)
                    throw new SimFSException(ExceptionType.InvalidDirectory);
            }
            else if (_inodeGlobalIndex < 0 || _fsMan == null || _inode.IsEmpty || _stream == null)
                throw new SimFSException(ExceptionType.InvalidDirectory);

            if (_stream != null && _stream.InodeInfo.globalIndex != _inodeGlobalIndex)
                throw new SimFSException(ExceptionType.InvalidDirectory, $"inode index doesn't match, dir: {_inodeGlobalIndex}, stream: {_stream.InodeInfo.globalIndex}");
        }

        public bool HasChild(ReadOnlySpan<char> fileName, out bool isDir)
        {
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(fileName);
            TryInitialize();
            isDir = false;
            using var _ = TempName(fileName, out var tempName);
            if (_dirs.ContainsKey(tempName))
            {
                isDir = true;
                return true;
            }
            return _files.ContainsKey(tempName);
        }

        public bool HasFile(ReadOnlySpan<char> fileName)
        {
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(fileName);
            TryInitialize();
            using var __ = TempName(fileName, out var tempName);
            return TryGetEntry(tempName, isDir: false, out _);
        }

        public SimFileStream GetFile(Transaction transaction, ReadOnlySpan<char> fileName, FileAccess access)
        {
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(fileName);
            TryInitialize();
            using var _ = TempName(fileName, out var tempName);
            var dataEntry = ThrowIfEntryNotFound(tempName, isDir: false);
            var inode = _fsMan.GetInode(dataEntry.inodeGlobalIndex, out var bg);
            return _fsMan.LoadFileStream(inode, access, transaction, this, bg);
        }

        public SimFileStream CreateFile(Transaction transaction, ReadOnlySpan<char> fileName, FileAccess access, int blockCount = -1)
        {
            ThrowsIfNotValid();
            if (blockCount <= 0)
                blockCount = 1;
            ThrowsIfNameIsInvalid(fileName);
            TryInitialize();
            using var _ = TempName(fileName, out var tempName);
            ThrowIfEntryExists(tempName, isDir: false);

            var childInode = _fsMan.AllocateInodeNear(transaction, _inodeGlobalIndex, InodeUsage.NormalFile, out var bg, blockCount);
            AddEntry(transaction, tempName.ToString(), childInode);
            return _fsMan.LoadFileStream(childInode, access, transaction, this, bg);
        }

        public SimFileStream GetOrCreateFile(Transaction transaction, ReadOnlySpan<char> fileName, FileAccess access, int blockCount = -1)
        {
            if (blockCount <= 0)
                blockCount = 1;
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(fileName);
            TryInitialize();
            using var _ = TempName(fileName, out var tempName);
            if (!TryGetEntry(tempName, isDir: false, out var entry))
            {
                var childInode = _fsMan.AllocateInodeNear(transaction, _inodeGlobalIndex, InodeUsage.NormalFile, out var bg1, blockCount);
                AddEntry(transaction, tempName.ToString(), childInode);
                return _fsMan.LoadFileStream(childInode, access, transaction, this, bg1);
            }

            var inode = _fsMan.GetInode(entry.inodeGlobalIndex, out var bg);
            return _fsMan.LoadFileStream(inode, access, transaction, this, bg);
        }

        public bool TryGetFile(Transaction transaction, ReadOnlySpan<char> fileName, FileAccess access, out SimFileStream fileStream)
        {
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(fileName);
            TryInitialize();
            fileStream = null;
            using var _ = TempName(fileName, out var tempName);
            if (!TryGetEntry(tempName, isDir: false, out var entry))
                return false;
            var inode = _fsMan.GetInode(entry.inodeGlobalIndex, out var bg);
            fileStream = _fsMan.LoadFileStream(inode, access, transaction, this, bg);
            return true;
        }

        public SimFileInfo GetFileInfo(ReadOnlySpan<char> fileName)
        {
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(fileName);
            TryInitialize();

            using var __ = TempName(fileName, out var tempName);
            var dataEntry = ThrowIfEntryNotFound(tempName, isDir: false, out var entryIndex);
            var inode = _fsMan.GetInode(dataEntry.inodeGlobalIndex, out _);
            return new SimFileInfo(_fsMan, _names[entryIndex], this, inode);
        }

        public bool TryGetFileInfo(ReadOnlySpan<char> fileName, out SimFileInfo info)
        {
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(fileName);
            TryInitialize();

            using var __ = TempName(fileName, out var tempName);
            info = default;
            if (!TryGetEntry(tempName, isDir: false, out var dataEntry, out var entryIndex))
                return false;
            var inode = _fsMan.GetInode(dataEntry.inodeGlobalIndex, out _);
            info = new SimFileInfo(_fsMan, _names[entryIndex], this, inode);
            return true;
        }

        public ReadOnlyMemory<char>[] GetFiles(OutPathKind pathKind = OutPathKind.Relative, bool topDirectoryOnly = true)
        {
            if (topDirectoryOnly)
            {
                if (pathKind == OutPathKind.Relative)
                    return Files.ToArray();
                var basePaths = SimUtil.Path.PathSegmentsHolder;
                GetFullNameSegments(basePaths);
                return Files.Select(x => SimUtil.Path.BuildPath(basePaths, x.Span)).ToArray();
            }
            else
            {
                var list = new List<ReadOnlyMemory<char>>();
                var basePaths = SimUtil.Path.PathSegmentsHolder;
                if (pathKind == OutPathKind.Absolute)
                    GetFullNameSegments(basePaths);
                GetAllChildren(basePaths, this, list, SimFSType.File);
                return list.ToArray();
            }
        }

        public void GetFiles(ICollection<ReadOnlyMemory<char>> paths, OutPathKind pathKind = OutPathKind.Relative, bool topDirectoryOnly = true)
        {
            if (topDirectoryOnly)
            {
                if (pathKind == OutPathKind.Relative)
                {
                    foreach (var file in Files)
                    {
                        paths.Add(file);
                    }
                    return;
                }
                var basePaths = SimUtil.Path.PathSegmentsHolder;
                GetFullNameSegments(basePaths);
                foreach (var file in Files.Select(x => SimUtil.Path.BuildPath(basePaths, x.Span)))
                {
                    paths.Add(file);
                }
            }
            else
            {
                var basePaths = SimUtil.Path.PathSegmentsHolder;
                if (pathKind == OutPathKind.Absolute)
                    GetFullNameSegments(basePaths);
                GetAllChildren(basePaths, this, paths, SimFSType.File);
            }
        }

        public bool HasDirectory(ReadOnlySpan<char> dirName)
        {
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(dirName);
            TryInitialize();
            using var __ = TempName(dirName, out var tempName);
            return TryGetEntry(tempName, true, out _, out _);
        }

        public SimDirectory GetDirectory(ReadOnlySpan<char> dirName)
        {
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(dirName);
            TryInitialize();

            using var _ = TempName(dirName, out var tempName);
            var entryData = ThrowIfEntryNotFound(tempName, isDir: true, out var entryIndex);
            return GetDirectory(entryData, entryIndex);
        }

        public SimDirectory CreateDirectory(Transaction transaction, ReadOnlySpan<char> dirName)
        {
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(dirName);
            TryInitialize();
            using var _ = TempName(dirName, out var tempName);
            ThrowIfEntryExists(tempName, isDir: true);
            return CreateDirectoryNoCheck(transaction, tempName);
        }

        public SimDirectory GetOrCreateDirectory(Transaction transaction, ReadOnlySpan<char> dirName)
        {
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(dirName);
            TryInitialize();
            using var _ = TempName(dirName, out var tempName);
            if (!TryGetEntry(tempName, isDir: true, out var entry, out var entryIndex))
            {
                return CreateDirectoryNoCheck(transaction, tempName);
            }

            return GetDirectory(entry, entryIndex);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private SimDirectory CreateDirectoryNoCheck(Transaction transaction, ReadOnlyMemory<char> dirName)
        {
            var nameClone = dirName.ToString();
            //SimLog.Log($"creating new dir: {nameClone}");
            var childInode = _fsMan.AllocateInodeNear(transaction, _inodeGlobalIndex, InodeUsage.Directory, out var bg);
            var entryIndex = AddEntry(transaction, nameClone, childInode);
            var dir = LoadSubDirectory(childInode, bg, nameClone.AsMemory(), entryIndex);
            return dir;
        }


        public ReadOnlyMemory<char>[] GetDirectories(OutPathKind pathKind = OutPathKind.Relative, bool topDirectoryOnly = true)
        {
            if (topDirectoryOnly)
            {
                if (pathKind == OutPathKind.Relative)
                    return SubDirectories.ToArray();
                var basePaths = SimUtil.Path.PathSegmentsHolder;
                GetFullNameSegments(basePaths);
                return SubDirectories.Select(x => SimUtil.Path.BuildPath(basePaths, x.Span)).ToArray();
            }
            else
            {
                var list = new List<ReadOnlyMemory<char>>();
                var basePaths = SimUtil.Path.PathSegmentsHolder;
                if (pathKind == OutPathKind.Absolute)
                    GetFullNameSegments(basePaths);
                GetAllChildren(basePaths, this, list, SimFSType.Directory);
                return list.ToArray();
            }
        }

        public void GetDirectories(ICollection<ReadOnlyMemory<char>> paths, OutPathKind pathKind = OutPathKind.Relative, bool topDirectoryOnly = true)
        {
            if (topDirectoryOnly)
            {
                if (pathKind == OutPathKind.Relative)
                {
                    foreach (var subDir in SubDirectories)
                    {
                        paths.Add(subDir);
                    }
                    return;
                }
                var basePaths = SimUtil.Path.PathSegmentsHolder;
                GetFullNameSegments(basePaths);
                foreach (var subDir in SubDirectories.Select(x => SimUtil.Path.BuildPath(basePaths, x.Span)))
                {
                    paths.Add(subDir);
                }
            }
            else
            {
                var basePaths = SimUtil.Path.PathSegmentsHolder;
                if (pathKind == OutPathKind.Absolute)
                    GetFullNameSegments(basePaths);
                GetAllChildren(basePaths, this, paths, SimFSType.Directory);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ReadOnlyMemory<char> CloneName(ReadOnlyMemory<char> tempName)
        {
            Memory<char> nameClone = new char[tempName.Length];
            tempName.CopyTo(nameClone);
            return nameClone;
        }

        public bool TryGetDirectory(ReadOnlySpan<char> dirName, out SimDirectory dir)
        {
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(dirName);
            TryInitialize();

            using var _ = TempName(dirName, out var tempName);
            dir = null;
            if (!TryGetEntry(tempName, isDir: true, out var dataEntry, out var entryIndex))
                return false;
            dir = GetDirectory(dataEntry, entryIndex);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private SimDirectory LoadSubDirectory(InodeInfo inode, BlockGroup bg, ReadOnlyMemory<char> name, int entryIndex)
        {
            var dir = _fsMan.Pooling.DirectoryPool.Get();
            dir.LoadInfo(_fsMan, this, inode, bg, name, entryIndex);
            _loadedDirectories[entryIndex] = dir;
            return dir;
        }


        private SimDirectory GetDirectory(DirectoryEntryData entryData, int entryIndex)
        {
            if (!_loadedDirectories.TryGetValue(entryIndex, out var subDir))
            {
                var inode = _fsMan.GetInode(entryData.inodeGlobalIndex, out var bg);
                var name = _names[entryIndex];
                subDir = LoadSubDirectory(inode, bg, name, entryIndex);
            }
            return subDir;
        }

        private int AddEntry(Transaction transaction, string name, InodeInfo inode)
        {
            inode.ThrowsIfNotValid();
            transaction.DirectoryBeforeChange(this, _entries.Count);
            var (inodeGlobalIndex, inodeData) = inode;
            var nameSpan = name.AsSpan();
            var entryLength = DirectoryEntryData.GetEntryLength(nameSpan.Length);
            var index = _unusedEntries.IndexOf((entryLength, null), false);
            if (index < 0)
                index = ~index;
            var entryIndex = -1;
            DirectoryEntryData entryData;
            if (index < _unusedEntries.Count)
            {
                var list = _unusedEntries[index].indices;
                entryIndex = list[^1];
                list.RemoveAt(list.Count - 1);
                if (list.Count <= 0)
                {
                    _unusedEntries.RemoveAt(index);
                    _fsMan.Pooling.IntListPool.Return(list);
                }
            }
            var nameMem = name.AsMemory();
            if (entryIndex >= 0)
            {
                entryData = _entries[entryIndex];
                transaction.DirectoryBeforeChange(this, entryIndex, entryData, _names[entryIndex]);
                entryData.ThrowsIfNotEmpty();
                entryData = entryData.ReUse(nameSpan.Length, inodeGlobalIndex, inodeData.usage);
                entryData.ThrowsIfNotValid();
                _entries[entryIndex] = entryData;
                _names[entryIndex] = nameMem;
            }
            else
            {
                entryIndex = _entries.Count;
                entryData = new DirectoryEntryData(entryLength, nameSpan.Length, inodeGlobalIndex, inodeData.usage);
                transaction.DirectoryBeforeChange(this, entryIndex, new DirectoryEntryData(entryLength, 0, inodeGlobalIndex, InodeUsage.Unused), nameMem);
                entryData.ThrowsIfNotValid();
                _entries.Add(entryData);
                _names.Add(nameMem);
            }
            (entryData.usage == InodeUsage.Directory ? _dirs : _files).Add(nameMem, entryIndex);
            return entryIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DeleteEntry(Transaction transaction, DirectoryEntryData entryData, int entryIndex, bool checkValid)
        {
            if (checkValid)
                entryData.ThrowsIfNotValid();
            transaction.DirectoryBeforeChange(this, _entries.Count);
            transaction.DirectoryBeforeChange(this, entryIndex, _entries[entryIndex], _names[entryIndex]);
            _fsMan.FreeInode(transaction, entryData.inodeGlobalIndex);
            DeleteEntryInternal(entryData, entryIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DeleteEntryInternal(DirectoryEntryData entryData, int entryIndex)
        {
            _names[entryIndex] = default;
            entryData = entryData.Free();
            var index = _unusedEntries.IndexOf((entryData.entryLength, null), false);
            if (index < 0)
            {
                var list = _fsMan.Pooling.IntListPool.Get();
                list.Add(entryIndex);
                _unusedEntries.Add((entryData.entryLength, list));
            }
            else
            {
                var (_, list) = _unusedEntries[index];
                list.Add(entryIndex);
            }
            _entries[entryIndex] = entryData;
        }

        public bool TryDeleteFile(Transaction transaction, ReadOnlySpan<char> name)
        {
            return TryDelete(transaction, name, false);
        }

        public bool TryDeleteDirectory(Transaction transaction, ReadOnlySpan<char> name)
        {
            return TryDelete(transaction, name, true);
        }

        public bool TryDeleteChild(Transaction transaction, ReadOnlySpan<char> name)
        {
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(name);
            TryInitialize();
            using var __ = TempName(name, out var tempName);
            var entryIndex = GetEntryIndex(tempName, out _);
            if (entryIndex < 0)
                return false;
            Delete(transaction, tempName, entryIndex, true);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryDelete(Transaction transaction, ReadOnlySpan<char> name, bool isDir)
        {
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(name);
            TryInitialize();
            using var _ = TempName(name, out var tempName);
            if (!TryGetEntry(tempName, isDir, out var entryData, out var entryIndex))
                return false;
            Delete(transaction, tempName, entryIndex, true);
            return true;
        }

        private void Delete(Transaction transaction, ReadOnlyMemory<char> fileName, int entryIndex, bool alsoDeleteName)
        {
            if ((uint)entryIndex > (uint)_entries.Count)
                throw new ArgumentOutOfRangeException(nameof(entryIndex));
            var entryData = _entries[entryIndex];
            if (entryData.usage == InodeUsage.Directory)
            {
                var dir = GetDirectory(entryData, entryIndex);
                dir.Clear(transaction);
                DeleteEntry(transaction, entryData, entryIndex, true);
                dir.SetAsDeleted();
                if (alsoDeleteName)
                    _dirs.Remove(fileName);
            }
            else if (entryData.usage == InodeUsage.NormalFile)
            {
                if (_fsMan.IsFileOpen(entryData.inodeGlobalIndex))
                    throw new SimFSException(ExceptionType.UnableToDeleteFile, $"file: {BuildFullName(fileName.Span)} is still open");

                DeleteEntry(transaction, entryData, entryIndex, true);
                if (alsoDeleteName)
                    _files.Remove(fileName);
            }
            else
                DeleteEntry(transaction, entryData, entryIndex, true);
        }

        public void Clear(Transaction transaction)
        {
            ThrowsIfNotValid();
            TryInitialize();
            foreach (var (name, i) in _dirs)
            {
                Delete(transaction, name, i, false);
            }
            _dirs.Clear();
            foreach (var (name, i) in _files)
            {
                Delete(transaction, name, i, false);
            }
            _files.Clear();
        }

        public bool TryMoveChild(Transaction transaction, ReadOnlySpan<char> name, SimDirectory targetDir, ReadOnlySpan<char> targetName, bool overwrite)
        {
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(name);
            TryInitialize();
            using var _ = TempName(name, out var fromName);
            using var __ = TempName(targetName, out var toName);
            var entryIndex = GetEntryIndex(fromName, out var isDir);
            if (entryIndex < 0)
                return false;
            var entryData = _entries[entryIndex];
            if (targetDir.MoveFrom(transaction, toName, entryData, overwrite))
            {
                Delete(transaction, fromName, entryIndex, true);
                return true;
            }
            return false;
        }

        private bool MoveFrom(Transaction transaction, ReadOnlyMemory<char> name, DirectoryEntryData entryData, bool overwrite)
        {
            var entryIndex = GetEntryIndex(name, out _);
            if (entryIndex >= 0)
            {
                if (overwrite)
                    Delete(transaction, name, entryIndex, true);
                else
                    return false;
            }
            var inode = _fsMan.GetInode(entryData.inodeGlobalIndex, out _);
            AddEntry(transaction, name.ToString(), inode);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetEntryIndex(ReadOnlyMemory<char> entryName, out bool isDir)
        {
            isDir = false;
            if (!_dirs.TryGetValue(entryName, out var entryIndex))
            {
                if (!_files.TryGetValue(entryName, out entryIndex))
                    return -1;
            }
            else
                isDir = true;
            return entryIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryGetEntry(ReadOnlyMemory<char> entryName, bool isDir, out DirectoryEntryData data)
            => TryGetEntry(entryName, isDir, out data, out _);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryGetEntry(ReadOnlyMemory<char> entryName, bool isDir, out DirectoryEntryData data, out int entryIndex)
        {
            var dict = isDir ? _dirs : _files;

            if (dict.TryGetValue(entryName, out entryIndex))
            {
                data = _entries[entryIndex];
                return true;
            }
            else
            {
                entryIndex = -1;
                data = default;
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfEntryExists(ReadOnlyMemory<char> entryName, bool isDir)
        {
            if (TryGetEntry(entryName, isDir, out _))
            {
                if (isDir)
                    throw new SimFSException(ExceptionType.DirectoryAlreadyExists, entryName.ToString());
                else
                    throw new SimFSException(ExceptionType.FileAlreadyExists, entryName.ToString());
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private DirectoryEntryData ThrowIfEntryNotFound(ReadOnlyMemory<char> entryName, bool isDir)
        {
            if (!TryGetEntry(entryName, isDir, out var d))
            {
                if (isDir)
                    throw new SimFSException(ExceptionType.DirectoryNotFound, entryName.ToString());
                else
                    throw new SimFSException(ExceptionType.FileNotFound, entryName.ToString());
            }
            return d;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private DirectoryEntryData ThrowIfEntryNotFound(ReadOnlyMemory<char> entryName, bool isDir, out int entryIndex)
        {
            if (!TryGetEntry(entryName, isDir, out var d, out entryIndex))
            {
                if (isDir)
                    throw new SimFSException(ExceptionType.DirectoryNotFound, entryName.ToString());
                else
                    throw new SimFSException(ExceptionType.FileNotFound, entryName.ToString());
            }
            return d;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowsIfNameIsInvalid(ReadOnlySpan<char> name)
        {
            if (name.IsEmpty)
                throw new SimFSException(ExceptionType.InvalidNameOrPath, nameof(name));
            foreach (var ch in InvalidChars)
            {
                if (name.IndexOf(ch) >= 0)
                    throw new SimFSException(ExceptionType.InvalidNameOrPath, name.ToString());
            }
        }

        private void TryInitialize()
        {
            if (_disposed)
                throw new SimFSException(ExceptionType.DirectoryAlreadyDisposed);
            if (_initialized)
                return;
            _stream.Position = 0;
            using var bufferHolder = _fsMan.Pooling.RentBuffer(out var buffer);
            _stream.Read(buffer[..4]);
            var entryCount = BitConverter.ToInt32(buffer[..4]);
            var length = _stream.Length;
            var read = 0;
            var bytesLeft = 0;
            while (read < length && _entries.Count < entryCount)
            {
                var loopRead = _stream.Read(buffer[bytesLeft..]);
                if (loopRead <= 0)
                    throw new SimFSException(ExceptionType.InvalidDirectory, "directory length and entry count don't match.");
                read += loopRead;
                if (read <= 0)
                    break;
                var readBuffer = buffer[..(loopRead + bytesLeft)];
                var dirRead = 0;
                do
                {
                    readBuffer = readBuffer[dirRead..];
                    if (readBuffer.Length <= 1)
                        break;
                    dirRead = DirectoryEntryData.ReadFromBuffer(readBuffer, out var entry, out var name);
                    if (dirRead > 0)
                    {
                        _names.Add(name.AsMemory());
                        _entries.Add(entry);
                        if (_entries.Count == entryCount)
                            break;
                    }
                } while (dirRead > 0);
                bytesLeft = readBuffer.Length;
                readBuffer.CopyTo(buffer);
            }
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                var name = _names[i];
                if (entry.usage == InodeUsage.Unused)
                {
                    DeleteEntryInternal(entry, i);
                    continue;
                }
                if (entry.usage == InodeUsage.Directory)
                    _dirs.Add(name, i);
                else
                    _files.Add(name, i);
            }
            _initialized = true;
        }

        private void SetAsDeleted()
        {
            if (_stream != null)
            {
                _stream.Dispose();
                _stream = null;
            }
            _inode = default;
            _deleted = true;
        }

        public void RevertChanges(int transactionId)
        {
            if (transactionData == null)
                return;
            if (!transactionData.TryGetValue(transactionId, out var selfTransData))
                return;
            ThrowsIfNotValid();
            if (_deleted)
            {
                var inodeInfo = _fsMan.GetInode(_inodeGlobalIndex, out var bg);
                _inode = inodeInfo.data;
                _stream = _fsMan.DangerouslyLoadFileStream(inodeInfo, FileAccess.ReadWrite, null, this, bg);
                _deleted = false;
            }

            RevertEntryChanges(selfTransData);

            DisposeTransactionData(transactionId);
        }

        private void RevertEntryChanges(DirectoryTransactionData selfTransData)
        {
            var entryCount = selfTransData.originEntryCount;
            if (entryCount < 0)
            {
                if ((selfTransData.entryChanges?.Count ?? 0) > 0)
                    throw new SimFSException(ExceptionType.InternalError, "entryCount < 0 but entryChanges > 0");
                return;
            }
            //SimLog.Log($"folder reverting: {BuildFullName()}");
            foreach (var (_, indicies) in _unusedEntries)
            {
                var len = indicies.Count;
                while (len-- > 0)
                {
                    if (indicies[len] >= entryCount)
                        indicies.RemoveAt(len);
                }
            }

            if (selfTransData.entryChanges != null)
            {
                foreach (var (entryIndex, changeData) in selfTransData.entryChanges)
                {
                    var (data, name) = changeData;
                    if (data.entryLength != _entries[entryIndex].entryLength)
                        throw new SimFSException(ExceptionType.InternalError, "directory entry length doesn't match");
                    var newData = _entries[entryIndex];
                    if (newData.usage == InodeUsage.Directory)
                    {
                        if (_loadedDirectories.TryGetValue(entryIndex, out var subDir))
                        {
                            //SimLog.Log($"reverting dir: {subDir.Name}, inode: {subDir._inodeGlobalIndex}");
                            subDir.SetAsDeleted();
                            subDir.Dispose();
                            _loadedDirectories.Remove(entryIndex);
                        }
                    }
                    if (entryIndex >= entryCount)
                        continue;
                    _entries[entryIndex] = data;
                    _names[entryIndex] = name;
                }
            }

            while (_entries.Count > entryCount)
            {
                _entries.RemoveAt(_entries.Count - 1);
                _names.RemoveAt(_names.Count - 1);
            }

            _dirs.Clear();
            _files.Clear();
            _unusedEntries.Clear();
            for (var index = 0; index < _entries.Count; index++)
            {
                var entry = _entries[index];
                switch (entry.usage)
                {
                    case InodeUsage.Unused:
                        DeleteEntryInternal(entry, index);
                        break;
                    case InodeUsage.Directory:
                        _dirs.Add(_names[index], index);
                        break;
                    case InodeUsage.TinyFile:
                    case InodeUsage.NormalFile:
                        _files.Add(_names[index], index);
                        break;
                }
            }
        }

        public void SaveChangesFillChildren(int TransactionId, List<(SimDirectory, bool)> stack)
        {
            if (transactionData == null)
                return;
            if (!transactionData.TryGetValue(TransactionId, out var selfTransData) || selfTransData.childrenChanges == null)
                return;
            foreach (var childEntryIndex in selfTransData.childrenChanges)
            {
                if (!_loadedDirectories.TryGetValue(childEntryIndex, out var subDir))
                    throw new SimFSException(ExceptionType.DirectoryNotFound, $"cannot apply changes to index: {childEntryIndex} of dir: {BuildFullName()}");
                stack.Add((subDir, false));
            }
        }

        public void SaveChanges(Transaction transaction, RangeList dirtyEntries)
        {
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));
            if (dirtyEntries == null)
                throw new ArgumentNullException(nameof(dirtyEntries));
            if (transactionData == null)
                return;

            if (!_deleted)
            {
                if (transactionData.TryGetValue(transaction.ID, out var selfTransData) && selfTransData.entryChanges != null)
                {
                    foreach (var (index, _) in selfTransData.entryChanges)
                    {
                        dirtyEntries.AddRange(index, 1);
                    }
                }
                if (dirtyEntries.Count <= 0)
                    return;

                Span<byte> entryCountSpan = stackalloc byte[4];
                BitConverter.TryWriteBytes(entryCountSpan, _entries.Count);
                _stream.Position = 0;
                _stream.WithTransaction(transaction);
                _stream.Write(entryCountSpan);

                var pos = 4;
                var curIndex = 0;
                using (var bufferHolder = _fsMan.Pooling.RentBuffer(out var buffer, 256))
                {
                    foreach (var (start, length) in dirtyEntries)
                    {
                        while (curIndex < start)
                        {
                            pos += _entries[curIndex].entryLength;
                            curIndex++;
                        }
                        for (; curIndex < start + length; curIndex++)
                        {
                            var entry = _entries[curIndex];
                            var name = _names[curIndex];
                            DirectoryEntryData.WriteToBuffer(buffer, entry, name);
                            _stream.Position = pos;
                            _stream.Write(buffer[..entry.entryLength]);

                            //if (entry.usage == InodeUsage.Directory)
                            //{
                            //    SimLog.Log($"applying dir: {name}");
                            //}

                            if (entry.usage == InodeUsage.Unused && _loadedDirectories.TryGetValue(curIndex, out var subDir))
                            {
                                subDir.Dispose();
                                _loadedDirectories.Remove(curIndex);
                            }

                            pos += entry.entryLength;
                        }
                    }
                }
                _stream.ClearTransaction();
            }
            DisposeTransactionData(transaction.ID);
        }

        private void DisposeTransactionData(int transactionId)
        {
            if (transactionData == null)
                return;
            if (!transactionData.TryGetValue(transactionId, out var selfTransData))
                return;

            var tp = _fsMan.Pooling.TransactionPooling;

            selfTransData.Dispose(tp);
            tp.DirTransDataPool.Return(selfTransData);
            transactionData.Remove(transactionId);

            var parent = Parent;
            var child = this;
            while (parent != null && (parent.transactionData?.TryGetValue(transactionId, out var parentTransData) ?? false))
            {
                var childrenChanges = parentTransData.childrenChanges;
                if (childrenChanges == null)
                    break;
                childrenChanges.Remove(child.Index);
                if (childrenChanges.Count > 0)
                    break;
                child = parent;
                parent = child.Parent;
            }
        }

        public void Dispose()
        {
            if (IsDirty)
                throw new SimFSException(ExceptionType.UnsaveChangesMade, BuildFullName().ToString());
            ThrowsIfNotValid(true, false);
            foreach (var (_, dir) in _loadedDirectories)
            {
                dir.Dispose();
            }
            _loadedDirectories.Clear();
            if (_stream != null)
            {
                _stream.Dispose();
                _stream = null;
            }
            foreach (var (_, list) in _unusedEntries)
            {
                _fsMan.Pooling.IntListPool.Return(list);
            }
            _fsMan.loadedDirectories--;
            _fsMan.Pooling.DirectoryPool.Return(this);
        }

        #region Enumerables
        internal readonly struct KeyEnumerable : IEnumerable<ReadOnlyMemory<char>>
        {
            public KeyEnumerable(Dictionary<ReadOnlyMemory<char>, int> dict)
            {
                _dict = dict;
            }

            private readonly Dictionary<ReadOnlyMemory<char>, int> _dict;
            public IEnumerator<ReadOnlyMemory<char>> GetEnumerator()
            {
                return new KeyEnumerator(_dict);
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        internal struct KeyEnumerator : IEnumerator<ReadOnlyMemory<char>>
        {
            public KeyEnumerator(Dictionary<ReadOnlyMemory<char>, int> dict)
            {
                _enumerator = dict.GetEnumerator();
                Current = default;
            }

            private Dictionary<ReadOnlyMemory<char>, int>.Enumerator _enumerator;

            public ReadOnlyMemory<char> Current { get; private set; }

            readonly object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_enumerator.MoveNext())
                {
                    Current = _enumerator.Current.Key;
                    return true;
                }
                Current = default;
                return false;
            }

            public void Reset()
            {
                (_enumerator as IEnumerator).Reset();
            }

            public void Dispose()
            {
            }
        }

        internal readonly struct AllEntryEnumerable : IEnumerable<ReadOnlyMemory<char>>
        {
            public AllEntryEnumerable(SimDirectory dir)
            {
                _dir = dir;
            }

            private readonly SimDirectory _dir;
            public IEnumerator<ReadOnlyMemory<char>> GetEnumerator() => new AllEntryEnumerator(_dir);

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        internal struct AllEntryEnumerator : IEnumerator<ReadOnlyMemory<char>>
        {
            public AllEntryEnumerator(SimDirectory dir)
            {
                _dirNames = dir._dirs.GetEnumerator();
                _fileNames = dir._files.GetEnumerator();
                Current = default;
            }

            private Dictionary<ReadOnlyMemory<char>, int>.Enumerator _dirNames;
            private Dictionary<ReadOnlyMemory<char>, int>.Enumerator _fileNames;

            public ReadOnlyMemory<char> Current { get; private set; }
            readonly object IEnumerator.Current => Current;

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                if (_dirNames.MoveNext())
                {
                    Current = _dirNames.Current.Key;
                    return true;
                }
                if (_fileNames.MoveNext())
                {
                    Current = _fileNames.Current.Key;
                    return true;
                }
                Current = default;
                return false;
            }

            void IEnumerator.Reset()
            {
                (_dirNames as IEnumerator).Reset();
                (_fileNames as IEnumerator).Reset();
                Current = null;
            }
        }
        #endregion
    }
}
