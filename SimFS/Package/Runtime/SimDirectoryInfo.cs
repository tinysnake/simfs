using System;
using System.Collections.Generic;

namespace SimFS
{
    public struct SimDirectoryInfo
    {
        internal SimDirectoryInfo(FSMan fsMan, SimDirectory dir)
        {
            _fsMan = fsMan;
            _dir = dir;
            if (dir != null)
                _fullPath = dir.BuildFullName();
            else
                _fullPath = null;
        }

        private readonly FSMan _fsMan;
        private SimDirectory _dir;
        private readonly ReadOnlyMemory<char> _fullPath;

        internal SimDirectory Directory => GetDirectory(false);

        internal SimDirectory GetDirectory(bool throwIfInvalid = false)
        {
            if (!_dir.IsValid)
            {
                _dir = FsManGetDirectory(_fsMan, _fullPath.Span);
                throw new SimFSException(ExceptionType.DirectoryNotFound, $"directory: {_fullPath} doesn't exist anymore!");
            }
            return _dir;
        }

        public readonly bool IsValid => _fsMan != null && !_fullPath.IsEmpty;
        public ReadOnlySpan<char> NameSpan => GetDirectory(true).Name.Span;
        public string Name => GetDirectory(true).Name.ToString();
        public readonly ReadOnlySpan<char> FullPathSpan => _fullPath.Span;
        public readonly string FullPath => _fullPath.ToString();

        public void GetFiles(ICollection<ReadOnlyMemory<char>> fileNames, OutPathKind pathKind = OutPathKind.Relative, bool topDirectoryOnly = true, bool throwsIfInvalid = false)
        {
            GetDirectory(throwsIfInvalid)?.GetFiles(fileNames, pathKind, topDirectoryOnly);
        }

        public ReadOnlyMemory<char>[] GetFiles(OutPathKind pathKind = OutPathKind.Relative, bool topDirectoryOnly = true, bool throwsIfInvalid = false)
        {

            return GetDirectory(throwsIfInvalid)?.GetFiles(pathKind, topDirectoryOnly) ?? Array.Empty<ReadOnlyMemory<char>>();
        }

        public ReadOnlyMemory<char>[] GetDirectories(OutPathKind pathKind = OutPathKind.Relative, bool topDirectoryOnly = true, bool throwsIfInvalid = false)
        {
            return GetDirectory(throwsIfInvalid)?.GetDirectories(pathKind, topDirectoryOnly) ?? Array.Empty<ReadOnlyMemory<char>>();
        }

        public void GetDirectories(ICollection<ReadOnlyMemory<char>> dirNames, OutPathKind pathKind = OutPathKind.Relative, bool topDirectoryOnly = true, bool throwsIfInvalid = false)
        {
            GetDirectory(throwsIfInvalid)?.GetDirectories(dirNames, pathKind, topDirectoryOnly);
        }

        private static SimDirectory FsManGetDirectory(FSMan fsMan, ReadOnlySpan<char> path)
        {
            if (path.CompareTo("/", StringComparison.Ordinal) == 0)
                return fsMan.RootDirectory;
            var parent = Filebase.GetParentDirectoryRelatively(null, fsMan.RootDirectory, path, out var dirName, out _, false);
            if (parent == null) return null;
            if (!parent.TryGetDirectory(dirName, out var subDir))
                return null;
            return subDir;
        }
    }
}