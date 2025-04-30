using System;
using System.Linq;

namespace SimFS
{
    public readonly struct SimFileDebugInfo
    {
        internal SimFileDebugInfo(int inodeGlobalIndex, BlockPointerData[] bpds, SimDirectory parentDir)
        {
            InodeGlobalIndex = inodeGlobalIndex;
            BlockPointers = bpds.Select(x => (x.globalIndex, x.blockCount)).ToArray();
            ParentDirInodeIndex = parentDir?.InodeInfo.globalIndex ?? -1;
        }

        public int InodeGlobalIndex { get; }
        public (int, byte)[] BlockPointers { get; }
        public int ParentDirInodeIndex { get; }
    }

    public readonly struct SimFileInfo
    {
        internal SimFileInfo(FSMan fsMan, ReadOnlyMemory<char> fileName, SimDirectory dir, InodeInfo inode)
        {
            inode.ThrowsIfNotValid();
            if (fileName.IsEmpty)
                throw new ArgumentNullException(nameof(fileName));
            _fsMan = fsMan ?? throw new ArgumentNullException(nameof(fsMan));
            _dirInfo = new SimDirectoryInfo(fsMan, dir);
            _fileName = fileName;
            (_inodeGlobalIndex, _inode) = inode;
        }

        private readonly FSMan _fsMan;
        private readonly SimDirectoryInfo _dirInfo;
        private readonly int _inodeGlobalIndex;
        private readonly InodeData _inode;
        private readonly ReadOnlyMemory<char> _fileName;

        public ReadOnlySpan<char> Name => _fileName.Span;

        public long Size => _inode.length;
        public ReadOnlySpan<byte> Attributes => _inode.attributes.AsSpan();

        public bool Exists => _fsMan.GetInode(_inodeGlobalIndex, out _).data.usage == InodeUsage.NormalFile;

        internal SimDirectory ParentDirectory => _dirInfo.GetDirectory(false);

        public SimFileStream OpenRead(bool throwsIfInvalid = false)
        {
            if (!Exists)
            {
                if (throwsIfInvalid)
                    throw new SimFSException(ExceptionType.FileNotFound, _fileName.ToString());
                return null;
            }
            return _fsMan.LoadFileStream(new InodeInfo(_inodeGlobalIndex, _inode), FileAccess.Read, null, _dirInfo.GetDirectory(throwsIfInvalid));
        }

        public SimFileStream OpenWrite(Transaction transaction, bool throwsIfInvalid = false)
        {
            if (!Exists)
            {
                if (throwsIfInvalid)
                    throw new SimFSException(ExceptionType.FileNotFound, _fileName.ToString());
                return null;
            }
            return _fsMan.LoadFileStream(new InodeInfo(_inodeGlobalIndex, _inode), FileAccess.ReadWrite, transaction, _dirInfo.GetDirectory(throwsIfInvalid));
        }
    }
}
