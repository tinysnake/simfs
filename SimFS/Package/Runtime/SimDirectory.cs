using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

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

        public static void GetAllChildren(List<ReadOnlyMemory<char>> basePaths, SimDirectory dir, List<ReadOnlyMemory<char>> list, SimFSType type)
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

        internal SimDirectory()
        {

        }

        internal void InPool()
        {
            if (_loadedDirectories.Count > 0)
                throw new SimFSException(ExceptionType.InternalError, "should first dispose all loaded Directories, then InPool");
            if (_stream != null)
                throw new SimFSException(ExceptionType.InternalError, "should first dispose self stream, then InPool");
            _inodeGlobalIndex = -1;
            _inode = default;
            _fsMan = null;
            _entries.Clear();
            _names.Clear();
            _files.Clear();
            _dirs.Clear();
            _unusedEntries.Clear();
            _dirtyEntries.Clear();
            _initialized = false;
            _disposed = true;
        }

        internal void LoadInfo(FSMan fsMan, SimDirectory parent, InodeInfo inode, BlockGroup bg, ReadOnlyMemory<char> name)
        {
            var isRootDir = parent == null && name.Span.CompareTo(ROOT_DIR_NAME, StringComparison.Ordinal) == 0;
            if (!isRootDir && parent == null)
                throw new ArgumentNullException(nameof(parent));
            if (!isRootDir)
                ThrowsIfNameIsInvalid(name.Span);
            inode.ThrowsIfNotValid();

            var comparer = fsMan.Customizer.NameComparer;
            _loadedDirectories ??= new Dictionary<ReadOnlyMemory<char>, SimDirectory>(comparer);
            _files ??= new Dictionary<ReadOnlyMemory<char>, int>(comparer);
            _dirs ??= new Dictionary<ReadOnlyMemory<char>, int>(comparer);

            _fsMan = fsMan;
            Parent = parent;
            Name = name;
            (_inodeGlobalIndex, _inode) = inode;
            _stream = _fsMan.Pooling.FileStreamPool.Get();
            _stream.LoadFileStream(fsMan, inode, bg);
            _fsMan.loadedDirectories++;
            if (_fsMan.loadedDirectories > _fsMan.Customizer.MaxCachedDirectoires)
                TrimLoadedDirectories();
            _disposed = false;
        }

        private int _inodeGlobalIndex;
        private InodeData _inode;
        private FSMan _fsMan;
        private SimFileStream _stream;
        private bool _initialized;
        private bool _disposed;

        private Dictionary<ReadOnlyMemory<char>, SimDirectory> _loadedDirectories = new(NameComparer.Ordinal);
        private Dictionary<ReadOnlyMemory<char>, int> _files = new(NameComparer.Ordinal);
        private Dictionary<ReadOnlyMemory<char>, int> _dirs = new(NameComparer.Ordinal);
        private readonly List<DirectoryEntryData> _entries = new();
        private readonly List<ReadOnlyMemory<char>> _names = new();
        private readonly SortedList<(int nameLength, List<int> indices)> _unusedEntries = new(UnusedEntryComparer.Default);
        private readonly RangeList _dirtyEntries = new();

        public ReadOnlyMemory<char> Name { get; private set; }
        public SimDirectory Parent { get; private set; }

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
        public void ThrowsIfNotValid()
        {
            if (_inodeGlobalIndex < 0 || _fsMan == null || _inode.IsEmpty || _stream == null)
                throw new SimFSException(ExceptionType.InvalidDirectory);
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

        public SimFileStream CreateFile(ReadOnlySpan<char> fileName, int blockCount = -1)
        {
            ThrowsIfNotValid();
            if (blockCount <= 0)
                blockCount = 1;
            ThrowsIfNameIsInvalid(fileName);
            TryInitialize();
            using var _ = TempName(fileName, out var tempName);
            ThrowIfEntryExists(tempName, isDir: false);

            var childInode = _fsMan.AllocateInodeNear(_inodeGlobalIndex, InodeUsage.NormalFile, out var bg, blockCount);
            AddEntry(tempName.ToString(), childInode);
            return _fsMan.LoadFileStream(childInode, bg);
        }

        public SimFileStream GetFile(ReadOnlySpan<char> fileName)
        {
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(fileName);
            TryInitialize();
            using var _ = TempName(fileName, out var tempName);
            var dataEntry = ThrowIfEntryNotFound(tempName, isDir: false);
            var inode = _fsMan.GetInode(dataEntry.inodeGlobalIndex, out var bg);
            return _fsMan.LoadFileStream(inode, bg);
        }

        public SimFileStream GetOrCreateFile(ReadOnlySpan<char> fileName, int blockCount = -1)
        {
            if (blockCount <= 0)
                blockCount = 1;
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(fileName);
            TryInitialize();
            using var _ = TempName(fileName, out var tempName);
            if (!TryGetEntry(tempName, isDir: false, out var entry))
            {
                var childInode = _fsMan.AllocateInodeNear(_inodeGlobalIndex, InodeUsage.NormalFile, out var bg1, blockCount);
                AddEntry(tempName.ToString(), childInode);
                return _fsMan.LoadFileStream(childInode, bg1);
            }

            var inode = _fsMan.GetInode(entry.inodeGlobalIndex, out var bg);
            return _fsMan.LoadFileStream(inode, bg);
        }

        public bool TryGetFile(ReadOnlySpan<char> fileName, out SimFileStream fileStream)
        {
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(fileName);
            TryInitialize();
            fileStream = null;
            using var _ = TempName(fileName, out var tempName);
            if (!TryGetEntry(tempName, isDir: false, out var entry))
                return false;
            var inode = _fsMan.GetInode(entry.inodeGlobalIndex, out var bg);
            fileStream = _fsMan.LoadFileStream(inode, bg);
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
            return new SimFileInfo(_fsMan, _names[entryIndex], inode);
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
            info = new SimFileInfo(_fsMan, _names[entryIndex], inode);
            return true;
        }

        public ReadOnlyMemory<char>[] GetFiles(PathKind pathKind = PathKind.Relative, bool topDirectoryOnly = true)
        {
            if (topDirectoryOnly)
            {
                if (pathKind == PathKind.Relative)
                    return Files.ToArray();
                var basePaths = SimUtil.Path.PathSegmentsHolder;
                GetFullNameSegments(basePaths);
                return Files.Select(x => SimUtil.Path.BuildPath(basePaths, x.Span)).ToArray();
            }
            else
            {
                var list = new List<ReadOnlyMemory<char>>();
                var basePaths = SimUtil.Path.PathSegmentsHolder;
                if (pathKind == PathKind.Absolute)
                    GetFullNameSegments(basePaths);
                GetAllChildren(basePaths, this, list, SimFSType.File);
                return list.ToArray();
            }
        }

        public void GetFiles(List<ReadOnlyMemory<char>> paths, PathKind pathKind = PathKind.Relative, bool topDirectoryOnly = true)
        {
            if (topDirectoryOnly)
            {
                if (pathKind == PathKind.Relative)
                {
                    paths.AddRange(Files);
                    return;
                }
                var basePaths = SimUtil.Path.PathSegmentsHolder;
                GetFullNameSegments(basePaths);
                paths.AddRange(Files.Select(x => SimUtil.Path.BuildPath(basePaths, x.Span)));
            }
            else
            {
                var basePaths = SimUtil.Path.PathSegmentsHolder;
                if (pathKind == PathKind.Absolute)
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

        public SimDirectory CreateDirectory(ReadOnlySpan<char> dirName)
        {
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(dirName);
            TryInitialize();
            using var _ = TempName(dirName, out var tempName);
            ThrowIfEntryExists(tempName, isDir: true);
            return CreateDirectoryNoCheck(tempName);
        }

        public SimDirectory GetDirectory(ReadOnlySpan<char> dirName)
        {
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(dirName);
            TryInitialize();

            using var _ = TempName(dirName, out var tempName);
            var entryData = ThrowIfEntryNotFound(tempName, isDir: true, out var entryIndex);
            return GetDirectory(tempName, entryData, entryIndex);
        }

        public SimDirectory GetOrCreateDirectory(ReadOnlySpan<char> dirName)
        {
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(dirName);
            TryInitialize();
            using var _ = TempName(dirName, out var tempName);
            if (!TryGetEntry(tempName, isDir: true, out var entry, out var entryIndex))
            {
                return CreateDirectoryNoCheck(tempName);
            }

            return GetDirectory(tempName, entry, entryIndex);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private SimDirectory CreateDirectoryNoCheck(ReadOnlyMemory<char> dirName)
        {
            var nameClone = dirName.ToString();
            var childInode = _fsMan.AllocateInodeNear(_inodeGlobalIndex, InodeUsage.Directory, out var bg);
            var dir = LoadSubDirectory(childInode, bg, nameClone.AsMemory());
            AddEntry(nameClone, childInode);
            return dir;
        }


        public ReadOnlyMemory<char>[] GetDirectories(PathKind pathKind = PathKind.Relative, bool topDirectoryOnly = true)
        {
            if (topDirectoryOnly)
            {
                if (pathKind == PathKind.Relative)
                    return SubDirectories.ToArray();
                var basePaths = SimUtil.Path.PathSegmentsHolder;
                GetFullNameSegments(basePaths);
                return SubDirectories.Select(x => SimUtil.Path.BuildPath(basePaths, x.Span)).ToArray();
            }
            else
            {
                var list = new List<ReadOnlyMemory<char>>();
                var basePaths = SimUtil.Path.PathSegmentsHolder;
                if (pathKind == PathKind.Absolute)
                    GetFullNameSegments(basePaths);
                GetAllChildren(basePaths, this, list, SimFSType.Directory);
                return list.ToArray();
            }
        }

        public void GetDirectories(List<ReadOnlyMemory<char>> paths, PathKind pathKind = PathKind.Relative, bool topDirectoryOnly = true)
        {
            if (topDirectoryOnly)
            {
                if (pathKind == PathKind.Relative)
                {
                    paths.AddRange(SubDirectories);
                    return;
                }
                var basePaths = SimUtil.Path.PathSegmentsHolder;
                GetFullNameSegments(basePaths);
                paths.AddRange(SubDirectories.Select(x => SimUtil.Path.BuildPath(basePaths, x.Span)));
            }
            else
            {
                var basePaths = SimUtil.Path.PathSegmentsHolder;
                if (pathKind == PathKind.Absolute)
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
            dir = GetDirectory(tempName, dataEntry, entryIndex);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private SimDirectory LoadSubDirectory(InodeInfo inode, BlockGroup bg, ReadOnlyMemory<char> name)
        {
            var dir = _fsMan.Pooling.DirectoryPool.Get();
            dir.LoadInfo(_fsMan, this, inode, bg, name);
            _loadedDirectories[name] = dir;
            return dir;
        }


        private SimDirectory GetDirectory(ReadOnlyMemory<char> dirName, DirectoryEntryData entryData, int entryIndex)
        {
            if (!_loadedDirectories.TryGetValue(dirName, out var subDir))
            {
                var inode = _fsMan.GetInode(entryData.inodeGlobalIndex, out var bg);
                var name = _names[entryIndex];
                subDir = LoadSubDirectory(inode, bg, name);
            }
            return subDir;
        }

        private void AddEntry(string name, InodeInfo inode)
        {
            inode.ThrowsIfNotValid();
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
                entryData.ThrowsIfNotValid();
                _entries.Add(entryData);
                _names.Add(nameMem);
            }
            _dirtyEntries.AddRange(entryIndex, 1);
            (entryData.usage == InodeUsage.Directory ? _dirs : _files).Add(nameMem, entryIndex);
        }

        private void DeleteEntry(DirectoryEntryData entryData, int entryIndex, bool checkValid = true)
        {
            if (checkValid)
                entryData.ThrowsIfNotValid();
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
            _dirtyEntries.AddRange(entryIndex, 1);
        }


        public bool TryDeleteFile(ReadOnlySpan<char> name)
        {
            return TryDelete(name, false);
        }

        public bool TryDeleteDirectory(ReadOnlySpan<char> name)
        {
            return TryDelete(name, true);
        }

        public bool TryDeleteChild(ReadOnlySpan<char> name)
        {
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(name);
            TryInitialize();
            using var __ = TempName(name, out var tempName);
            var entryIndex = GetEntryIndex(tempName, out _);
            if (entryIndex < 0)
                return false;
            Delete(tempName, entryIndex, true);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryDelete(ReadOnlySpan<char> name, bool isDir)
        {
            ThrowsIfNotValid();
            ThrowsIfNameIsInvalid(name);
            TryInitialize();
            using var _ = TempName(name, out var tempName);
            if (!TryGetEntry(tempName, isDir, out var entryData, out var entryIndex))
                return false;
            Delete(tempName, entryIndex, true);
            return true;
        }

        private void Delete(ReadOnlyMemory<char> fileName, int entryIndex, bool alsoDeleteName)
        {
            if ((uint)entryIndex > (uint)_entries.Count)
                throw new ArgumentOutOfRangeException(nameof(entryIndex));
            var entryData = _entries[entryIndex];
            if (entryData.usage == InodeUsage.Directory)
            {
                var dir = GetDirectory(fileName, entryData, entryIndex);
                dir.Clear();
                dir.Dispose();
                _loadedDirectories.Remove(fileName);
                if (alsoDeleteName)
                    _dirs.Remove(fileName);
            }
            else
            {
                if (alsoDeleteName)
                    _files.Remove(fileName);
            }
            _fsMan.FreeInode(entryData.inodeGlobalIndex);
            DeleteEntry(entryData, entryIndex);
        }

        public void Clear()
        {
            ThrowsIfNotValid();
            TryInitialize();
            foreach (var (name, i) in _dirs)
            {
                Delete(name, i, false);
            }
            _dirs.Clear();
            foreach (var (name, i) in _files)
            {
                Delete(name, i, false);
            }
            _files.Clear();
        }

        public bool TryMoveChild(ReadOnlySpan<char> name, SimDirectory targetDir, ReadOnlySpan<char> targetName, bool overwrite)
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
            if (targetDir.MoveFrom(toName, entryData, overwrite))
            {
                Delete(fromName, entryIndex, true);
                return true;
            }
            return false;
        }

        private bool MoveFrom(ReadOnlyMemory<char> name, DirectoryEntryData entryData, bool overwrite)
        {
            var entryIndex = GetEntryIndex(name, out _);
            if (entryIndex >= 0)
            {
                if (overwrite)
                    Delete(name, entryIndex, true);
                else
                    return false;
            }
            var inode = _fsMan.GetInode(entryData.inodeGlobalIndex, out _);
            AddEntry(name.ToString(), inode);
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
                throw new SimFSException(ExceptionType.InternalError, "directory already disposed");
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
                read += loopRead;
                if (read <= 0)
                    break;
                var readBuffer = buffer[..(loopRead + bytesLeft)];
                var dirRead = 0;
                do
                {
                    readBuffer = readBuffer[dirRead..];
                    if (readBuffer.Length <= 0)
                        break;
                    dirRead = ReadEntry(readBuffer);
                    if (_entries.Count == entryCount)
                        break;
                } while (dirRead > 0);
                bytesLeft = readBuffer.Length;
                readBuffer.CopyTo(buffer);
            }
            //_originalLength = 0;
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                var name = _names[i];
                if (entry.usage == InodeUsage.Unused)
                {
                    DeleteEntry(entry, i, false);
                    continue;
                }
                if (entry.usage == InodeUsage.Directory)
                    _dirs.Add(name, i);
                else
                    _files.Add(name, i);
                //_originEndPos += entry.entryLength;
            }
            //_originalLength = _entries.Count;
            _initialized = true;
        }

        private int ReadEntry(ReadOnlySpan<byte> buffer)
        {
            var entryLength = buffer[0];
            if (buffer.Length < entryLength)
                return 0;
            var nameLength = buffer[1];
            var usage = (InodeUsage)buffer[2];
            var inodeLocation = BitConverter.ToInt32(buffer[3..]);
            var nameBytes = buffer.Slice(7, nameLength);

            var name = usage > InodeUsage.Unused ? Encoding.UTF8.GetString(nameBytes[..nameLength]) : null;
            _names.Add(name.AsMemory());
            var entry = new DirectoryEntryData(entryLength, nameLength, inodeLocation, usage);
            _entries.Add(entry);

            return entryLength;
        }

        private void WriteEntry(int position, ReadOnlyMemory<char> name, DirectoryEntryData entryData)
        {
            _stream.Position = position;
            using var bufferHolder = _fsMan.Pooling.RentBuffer(out var span, entryData.entryLength);
            span = span[..entryData.entryLength];
            span[0] = entryData.entryLength;
            span[1] = (byte)Encoding.UTF8.GetBytes(name.Span, span[7..]);
            span[2] = (byte)entryData.usage;
            BitConverter.TryWriteBytes(span[3..], entryData.inodeGlobalIndex);
            _stream.Write(span);
        }
        private void TrySaveChanges()
        {
            if (_dirtyEntries.Count <= 0)
                return;
            Span<byte> entryCountSpan = stackalloc byte[4];
            _stream.Position = 0;
            BitConverter.TryWriteBytes(entryCountSpan, _entries.Count);
            _stream.Position = 0;
            _stream.Write(entryCountSpan);

            var pos = 4;
            var curIndex = 0;
            foreach (var (start, length) in _dirtyEntries)
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
                    WriteEntry(pos, name, entry);
                    pos += entry.entryLength;
                }
            }
        }

        public void Dispose()
        {
            ThrowsIfNotValid();
            foreach (var (_, dir) in _loadedDirectories)
            {
                dir.Dispose();
            }
            _loadedDirectories.Clear();
            TrySaveChanges();
            _stream.Dispose();
            _stream = null;
            foreach (var (_, list) in _unusedEntries)
            {
                _fsMan.Pooling.IntListPool.Return(list);
            }
            _fsMan.loadedDirectories--;
            _fsMan.Pooling.DirectoryPool.Return(this);
        }

        private void TrimLoadedDirectories()
        {
            // this is my self made "algorithm" of trimming the directories, probably performs badly,
            // but "should" smarter than just trimming from the top
            var targetTrimNum = _fsMan.Customizer.MaxCachedDirectoires / 10;
            var trimCount = 0;
            // first find out current dir chain
            _trimTempList.Clear();
            var parent = this;
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
                // the more loaded directories means the less important the sub-directories are.
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
            var keysList = new List<ReadOnlyMemory<char>>();
            for (; middlePt < _trimTempList.Count - 1; middlePt++)
            {
                var loadedDirs = _trimTempList[middlePt]._loadedDirectories;
                keysList.AddRange(loadedDirs.Select(x => x.Key));
                var childDir = _trimTempList[middlePt + 1];
                foreach (var key in keysList)
                {
                    if (NameComparer.Ordinal.Equals(key, childDir.Name))
                        continue;
                    var num = _fsMan.loadedDirectories;
                    loadedDirs[key].Dispose();
                    loadedDirs.Remove(key);
                    trimCount += num - _fsMan.loadedDirectories;
                    if (trimCount >= targetTrimNum)
                        return;
                }
                keysList.Clear();
            }
            throw new SimFSException(ExceptionType.InternalError, "unexpected behavior on directory triming");
        }

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
    }
}
