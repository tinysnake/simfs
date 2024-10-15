using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SimFS
{
    internal partial class FSMan : IDisposable
    {
        private const int MIN_BUFFER_SIZE = 4096;

        private byte[] _inodeBitmapBuffer;
        private byte[] _blockBitmapBuffer;
        private InodeData[] _inodeTableBuffer;
        private readonly Stream _fs;
        private WriteBuffer _writeBuffer;
        private byte _inodeSize;
        private ReadWriteLock _rwLock;

        public WriteBuffer WriteBuffer => _writeBuffer;

        private FSHead SetupFileSystem(FSHeadData data, int bufferSize)
        {
            if (bufferSize < 1024)
                throw new ArgumentException("bufferSize too small");
            bufferSize = Math.Max(bufferSize, Math.Max(MIN_BUFFER_SIZE, data.blockSize * 8));
            _writeBuffer = new WriteBuffer(bufferSize, _fs, _rwLock);
            CheckFileEmpty();
            data.ThrowIfNotValid();
            WriteHead(data);
            var head = new FSHead(data);
            _inodeSize = (byte)InodeData.GetInodeSize(head.InodeBlockPointersCount, head.AttributeSize);
            InitBlockGroupBuffer(head);
            return head;
        }

        private FSHead InitializeFileSystem()
        {
            var data = ReadHead();
            data.ThrowIfNotValid();
            var head = new FSHead(data);
            InitBlockGroupBuffer(head);
            return head;
        }

        private void InitBlockGroupBuffer(FSHead head)
        {
            var blockSize = head.BlockSize;
            _inodeBitmapBuffer = new byte[blockSize];
            _blockBitmapBuffer = new byte[blockSize];
            _inodeTableBuffer = new InodeData[BlockGroup.GetInodeTableItemsCount(blockSize)];
            Pooling.MaxBufferSize = Math.Min(MIN_BUFFER_SIZE, blockSize * 8);
            Pooling.BlockPointersCount = head.InodeBlockPointersCount;
            Pooling.AttributeSize = head.AttributeSize;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CheckFileEmpty()
        {
            if (_fs.Length > 0)
                throw new SimFSException(ExceptionType.FileIsNotEmpty);
        }

        private FSHeadData ReadHead()
        {
            using var bufferHolder = Pooling.RentBuffer(out var span, FSHeadData.RESERVED_SIZE);
            _fs.Position = 0;
            _fs.Read(span);
            var sig = span[..FSHeadData.SIGNATURE.Length];
            if (!sig.SequenceEqual(FSHeadData.SIGNATURE))
                throw new SimFSException(ExceptionType.UnknownFileFormat);
            span = span[sig.Length..];
            var head = MemoryMarshal.Read<FSHeadData>(span);
            if (head.version != FSHeadData.VERSION || head.blockSize == 0 ||
                head.blockSize != SimUtil.Number.NextPowerOf2(head.blockSize) ||
                head.inodeBlockPointersCount != FSHeadData.GetPointersSize(head.blockSize))
            {
                throw new SimFSException(ExceptionType.UnknownFileFormat);
            }
            var bufferSize = Math.Min(MIN_BUFFER_SIZE, head.blockSize * 8);
            if (_writeBuffer == null || bufferSize > _writeBuffer?.Size)
            {
                _writeBuffer = new WriteBuffer(bufferSize, _fs, _rwLock);
            }
            _inodeSize = (byte)InodeData.GetInodeSize(head.inodeBlockPointersCount, head.attributeSize);
            return head;
        }

        private void WriteHead(FSHeadData head)
        {
            _writeBuffer ??= new WriteBuffer(1024, _fs, _rwLock);
            var section = _writeBuffer.Rent(0, FSHeadData.RESERVED_SIZE);
            var bytes = section.bytes.Span;
            FSHeadData.SIGNATURE.CopyTo(bytes);
            bytes = bytes[FSHeadData.SIGNATURE.Length..];
            MemoryMarshal.Write(bytes, ref head);
            bytes = bytes[FSHeadData.MemSize..];
            bytes.Clear();
        }

        private void InitializeBlockGroup(BlockGroup bg, int blockGroupIndex, bool force)
        {
            _head.ThrowIfNotValid();
            var pos = BlockGroup.GetBlockGroupLocation(blockGroupIndex, _head.BlockSize, _inodeSize);
            if (!force && _fs.Length > pos)
            {
                CheckSpaceEmpty(pos);
            }
            var blockSize = _head.BlockSize;
            var section = _writeBuffer.Rent(pos, BlockGroupHead.RESERVED_SIZE);
            var bytes = section.bytes.Span;
            BlockGroupHead.SIGNATURE.CopyTo(bytes);
            var endOfBlock = pos + 8L * blockSize * blockSize;
            if (_fs.Length < endOfBlock)
                _fs.SetLength(endOfBlock);
            bg.Initialize(this, blockGroupIndex, blockSize, _inodeSize);
            _head.BlockGroupCount += 1;
            WriteHead(_head.ToHeadData());
            UpdateBlockGroupMeta(bg);
        }

        private BlockGroupHead ReadBlockGroupHead(int blockGroupIndex)
        {
            if ((uint)blockGroupIndex >= (uint)_head.BlockGroupCount)
                throw new IndexOutOfRangeException(nameof(blockGroupIndex));
            var loc = BlockGroup.GetBlockGroupLocation(blockGroupIndex, _head.BlockSize, _inodeSize);
            return ReadBlockGroupHeadOnLocation(loc);
        }

        private void ReadBlockGroup(BlockGroup bg, int blockGroupIndex)
        {
            var blockSize = _head.BlockSize;
            var loc = BlockGroup.GetBlockGroupLocation(blockGroupIndex, blockSize, _inodeSize);
            var gh = ReadBlockGroupHeadOnLocation(loc);
            using var _ = ReadWriteLocker.BeginRead(_rwLock);
            _fs.Read(_blockBitmapBuffer);
            _fs.Read(_inodeBitmapBuffer);
            var inodeTableItemsCount = BlockGroup.GetInodeTableItemsCount(blockSize);
            var inodeIndex = 0;
            using (var bufferHolder = Pooling.RentBuffer(out var buffer))
            {
                while (inodeIndex < inodeTableItemsCount)
                {
                    var targetNums = inodeTableItemsCount - inodeIndex;
                    var nums = Math.Min(targetNums, buffer.Length / _inodeSize);
                    var span = buffer[..(nums * _inodeSize)];
                    _fs.Read(span);
                    for (var j = 0; j < nums; j++)
                    {
                        var inodeSpan = span.Slice(j * _inodeSize, _inodeSize);
                        var inode = ReadInode(inodeSpan);
                        _inodeTableBuffer[inodeIndex++] = inode;
                    }
                }
            }
            bg.Load(this, blockGroupIndex, gh, _blockBitmapBuffer, _inodeBitmapBuffer, _inodeTableBuffer, _inodeSize);
        }

        public void UpdateBlockGroupMeta(BlockGroup bg)
        {
            var pos = BlockGroup.GetBlockGroupLocation(bg.GroupIndex, _head.BlockSize, _inodeSize);
            var section = _writeBuffer.Rent(pos, BlockGroupHead.RESERVED_SIZE + bg.BlockBitmap.RawLength + bg.InodeBitmap.RawLength);
            var buffer = section.bytes.Span;
            var headBuffer = buffer[..BlockGroupHead.RESERVED_SIZE];
            BlockGroupHead.SIGNATURE.CopyTo(headBuffer);
            headBuffer = headBuffer[BlockGroupHead.SIGNATURE.Length..];
            var bgHead = bg.Head;
            MemoryMarshal.Write(headBuffer, ref bgHead);
            headBuffer = headBuffer[BlockGroupHead.MemSize..];
            headBuffer.Clear();
            buffer = buffer[BlockGroupHead.RESERVED_SIZE..];
            var blockBimapBytes = bg.BlockBitmap.GetBytes().AsSpan();
            blockBimapBytes.CopyTo(buffer);
            buffer = buffer[blockBimapBytes.Length..];
            var inodeBitmapBytes = bg.InodeBitmap.GetBytes().AsSpan();
            inodeBitmapBytes.CopyTo(buffer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CheckSpaceEmpty(long location)
        {
            using var bufferHolder = Pooling.RentBuffer(out var span, BlockGroupHead.RESERVED_SIZE);
            _fs.Position = location;
            var bytes = span[..BlockGroupHead.RESERVED_SIZE];
            _fs.Read(bytes);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != 0)
                    throw new SimFSException(ExceptionType.SpaceIsNotEmpty, $"the space now checking at location: 0x{location:X} is not empty");
            }
        }

        private BlockGroupHead ReadBlockGroupHeadOnLocation(long location)
        {
            Flush();
            using var _ = ReadWriteLocker.BeginRead(_rwLock);
            using var bufferHolder = Pooling.RentBuffer(out var span, BlockGroupHead.RESERVED_SIZE);
            _fs.Position = location;
            var bytes = span[..BlockGroupHead.RESERVED_SIZE];
            _fs.Read(bytes);
#if DEBUG
            var sig = bytes[..BlockGroupHead.SIGNATURE.Length];
            if (!sig.SequenceEqual(BlockGroupHead.SIGNATURE))
                throw new SimFSException(ExceptionType.InvalidBlockGroup, $"the space now checking at location: 0x{location:X} is expecting a block group, but is not.");
#endif
            bytes = bytes[BlockGroupHead.SIGNATURE.Length..];
            var bgHead = MemoryMarshal.Read<BlockGroupHead>(bytes);
#if DEBUG
            bytes = bytes[BlockGroupHead.MemSize..];
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != 0)
                    throw new SimFSException(ExceptionType.InvalidBlockGroup, $"the space now checking at location: 0x{location:X} is expecting a block group, but is not.");
            }
#endif
            return bgHead;
        }

        public InodeData ReadInode(ReadOnlySpan<byte> span)
        {
            TryFlush();
            using var _ = ReadWriteLocker.BeginRead(_rwLock);
            var length = BitConverter.ToInt32(span);
            span = span[4..];
            var usage = (InodeUsage)span[0];
            span = span[1..];
            var attrSize = _head.AttributeSize;
            Span<byte> attr = stackalloc byte[attrSize];
            if (attrSize > 0)
            {
                span[..attrSize].CopyTo(attr);
                span = span[attrSize..];
            }
            var bpds = new BlockPointerData[_head.InodeBlockPointersCount];
            for (var i = 0; i < _head.InodeBlockPointersCount; i++)
            {
                bpds[i] = MemoryMarshal.Read<BlockPointerData>(span[(i * BlockPointerData.MemSize)..]);
            }
            if (usage == InodeUsage.Unused)
                return new InodeData(length, usage, null, null);
            else
                return new InodeData(length, usage, attr.ToArray(), bpds);
        }

        public void WriteInode(int blockGroupIndex, int inodeIndex, InodeData data)
        {
            var blockSize = _head.BlockSize;
            var loc = BlockGroup.GetInodeLocation(blockGroupIndex, blockSize, _inodeSize, inodeIndex);
            var sec = _writeBuffer.Rent(loc, _inodeSize);
            var span = sec.bytes.Span;
            BitConverter.TryWriteBytes(span, data.length);
            span = span[4..];
            BitConverter.TryWriteBytes(span, (byte)data.usage);
            span = span[1..];
            if (data.attributes != null && data.attributes.Length > 0)
            {
                data.attributes.AsSpan().CopyTo(span);
                span = span[data.attributes.Length..];
            }
            if (data.blockPointers != null && data.blockPointers.Length > 0)
            {
                for (var i = 0; i < data.blockPointers.Length; i++)
                {
                    var bpd = data.blockPointers[i];
                    MemoryMarshal.Write(span, ref bpd);
                    span = span[BlockPointerData.MemSize..];
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadRawData(long fileLocation, Span<byte> buffer)
        {
            TryFlush();
            using var _ = ReadWriteLocker.BeginRead(_rwLock);
            _fs.Position = fileLocation;
            return _fs.Read(buffer);
        }

        public void Backup(Stream stream, Span<byte> buffer)
        {
            if (stream == null || !stream.CanWrite)
                throw new ArgumentException("stream is invalid");
            if (_rwLock.State != ReadWriteState.None)
                throw new InvalidOperationException("cannot backup file system while reading or writing data.");
            SaveChanges();
            using var _ = ReadWriteLocker.BeginRead(_rwLock);
            if (buffer.IsEmpty)
                Pooling.RentBuffer(out buffer);
            _fs.Position = 0;
            var read = 0;
            do
            {
                read = _fs.Read(buffer);
                if (read > 0)
                    stream.Write(buffer[..read]);
            }
            while (read > 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TryFlush()
        {
            if (_rwLock.State == ReadWriteState.Reading)
                return;
            _writeBuffer.Flush();
        }

        public void Flush()
        {
            TryFlush();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SaveChanges()
        {
            if (_rwLock.State == ReadWriteState.Reading)
                return false;

            using var _ = ReadWriteLocker.BeginWrite(_rwLock);
            var result = true;
            foreach (var (_, bg) in _loadedBlockGroups)
            {
                result |= bg.SaveChanges();
            }

            result |= _rootDirectory.SaveChanges();
            TryFlush();
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DisposeIO()
        {
            _writeBuffer.Flush();
            _fs.Dispose();
        }
    }
}
