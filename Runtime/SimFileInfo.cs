using System;

namespace SimFS
{
    public readonly struct SimFileInfo
    {
        internal SimFileInfo(FSMan fsMan, ReadOnlyMemory<char> fileName, InodeInfo inode)
        {
            inode.ThrowsIfNotValid();
            if (fileName.IsEmpty)
                throw new ArgumentNullException(nameof(fileName));
            _fsMan = fsMan ?? throw new ArgumentNullException(nameof(fsMan));
            _fileName = fileName;
            (_inodeGlobalIndex, _inode) = inode;
        }

        private readonly FSMan _fsMan;
        private readonly int _inodeGlobalIndex;
        private readonly InodeData _inode;
        private readonly ReadOnlyMemory<char> _fileName;

        public ReadOnlySpan<char> Name => _fileName.Span;

        public long Size => _inode.length;
        public ReadOnlySpan<byte> Attributes => _inode.attributes.AsSpan();

        public bool Exists => _inodeGlobalIndex >= 0 && _inode.usage > InodeUsage.Unused;

        public SimFileStream Open() => _fsMan.LoadFileStream(new InodeInfo(_inodeGlobalIndex, _inode));
    }
}
