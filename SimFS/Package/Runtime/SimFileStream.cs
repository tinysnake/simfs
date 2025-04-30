using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace SimFS
{
    internal class FileStreamWriteComparer : IComparer<(int, BufferHolder<byte>)>
    {
        public static FileStreamWriteComparer Default { get; } = new();
        public int Compare((int, BufferHolder<byte>) x, (int, BufferHolder<byte>) y) => x.Item1 - y.Item1;
    }

    public class SimFileStream : Stream
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetMaxFileSize(int blockPointersCount, int blockSize)
        {
            return GetMaxBlocksCanAllocate(blockPointersCount) * blockSize;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetMaxBlocksCanAllocate(int blockPointersCount)
        {
            return blockPointersCount * byte.MaxValue;
        }

        internal SimFileStream()
        {
            InPool();
        }

        ~SimFileStream()
        {
            _fsMan?.CloseFile(_inodeGlobalIndex, this, _fileAccess);
        }

        internal void LoadFileStreamWithoutParent(FSMan fsMan, InodeInfo inodeInfo, FileAccess access, BlockGroup bg = null)
        {
            LoadFileStreamInternal(fsMan, inodeInfo, access, null, bg);
        }

        internal void LoadFileStream(FSMan fsMan, InodeInfo inodeInfo, FileAccess access, SimDirectory parentDir, BlockGroup bg = null)
        {
            if (parentDir == null || !parentDir.IsValid)
                throw new SimFSException(ExceptionType.InvalidDirectory);
            LoadFileStreamInternal(fsMan, inodeInfo, access, parentDir, bg);
        }

        private void LoadFileStreamInternal(FSMan fsMan, InodeInfo inodeInfo, FileAccess access, SimDirectory parentDir, BlockGroup bg = null)
        {
            inodeInfo.ThrowsIfNotValid();
            _fileAccess = access;
            var (inodeGlobalIndex, inodeData) = inodeInfo;
            _fsMan = fsMan ?? throw new ArgumentNullException(nameof(fsMan));
            _blockSize = fsMan.Head.BlockSize;
            _inodeGlobalIndex = inodeGlobalIndex;
            _length = inodeData.length;
            _blockPointers = inodeData.blockPointers;
            _attributes = inodeData.attributes;
            _parentDir = new SimDirectoryInfo(_fsMan, parentDir);
            var firstBpd = inodeData.blockPointers[0];
            if (firstBpd.IsEmpty)
                throw new SimFSException(ExceptionType.InvalidInode, "inode doesn't preallocate any block, it's empty");
            var (bgIndex, _) = FSMan.GetLocalIndex(inodeInfo.globalIndex, _blockSize);
            if (bg == null)
            {
                _inodeBg = fsMan.GetBlockGroup(bgIndex);
            }
            else
            {
                bg.ThrowsIfNotValid();
                if (bg.GroupIndex != bgIndex)
                    throw new SimFSException(ExceptionType.BlockGroupNotTheSame, $"inode group: {bgIndex}, bg index: {bg.GroupIndex}");
                _inodeBg = bg;
            }
            _localBlockIndex = 0;
            _localByteIndex = 0;
            _sharingData = _fsMan.TryOpenFile(_inodeGlobalIndex, this, _fileAccess);
        }

        internal void InPool()
        {
            _fileAccess = FileAccess.Read;
            _transaction = null;
            _blockSize = 0;
            _fsMan = null;
            _inodeBg = null;
            _localBlockIndex = -1;
            _localByteIndex = -1;
            _isWritten = false;
        }

        internal void WithTransaction(Transaction transaction)
        {
            if (_transaction != null)
                throw new SimFSException(ExceptionType.TransactionAlreadySet);
            _transaction = transaction;
        }

        internal void ClearTransaction()
        {
            _transaction = null;
        }


        private FileAccess _fileAccess;
        private SimDirectoryInfo _parentDir;
        private int _length;
        private BlockPointerData[] _blockPointers;
        private byte[] _attributes;
        private ushort _blockSize;
        private FSMan _fsMan;
        private int _inodeGlobalIndex;
        private BlockGroup _inodeBg;
        private int _localBlockIndex;
        private int _localByteIndex;
        private Transaction _transaction;
        private FileSharingData _sharingData;
        private bool _isWritten;

        public override bool CanWrite => _fileAccess > FileAccess.Read;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override long Length => _length;
        public override long Position { get => _localBlockIndex * _blockSize + _localByteIndex; set => Goto(value); }

        public bool IsValid => _fsMan != null;
        public SimDirectoryInfo DirectoryInfo => _parentDir;

        internal InodeInfo InodeInfo => new(_inodeGlobalIndex, new(_length, InodeUsage.NormalFile, _attributes, _blockPointers));

        public SimFileDebugInfo GetDebugInfo() => new(_inodeGlobalIndex, _blockPointers, _parentDir.GetDirectory(false));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowsIfNotValidRead()
        {
            if (_sharingData.WriteAccessInstance?._isWritten ?? false)
                throw new SimFSException(ExceptionType.NoReadWhenContentChanges);
            ThrowsIfNotValid();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowsIfNotValidWrite()
        {
            if (_fileAccess < FileAccess.ReadWrite)
                throw new SimFSException(ExceptionType.NoWriteAccessRight);
            if (_transaction == null)
                throw new SimFSException(ExceptionType.MissingTransaction);
            ThrowsIfNotValid();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowsIfNotValid()
        {
            if (_blockSize == 0 || _fsMan == null || _localBlockIndex < 0 || _localBlockIndex < 0)
                throw new SimFSException(ExceptionType.InvalidFileStream);
        }

        public void ReadAttributes(Span<byte> buffer)
        {
            ThrowsIfNotValid(); // attribute changes
            if (buffer.Length < _attributes.Length)
                throw new ArgumentException("buffer size is too small");
            _attributes.CopyTo(buffer);
        }

        public void WriteAttributes(ReadOnlySpan<byte> data)
        {
            ThrowsIfNotValidWrite();
            if (data.Length < _attributes.Length)
                throw new ArgumentException("buffer size is too small");
            _transaction.FileBeforeChange(_inodeGlobalIndex, _length);
            _transaction.FileAttributesBeforeChange(_inodeGlobalIndex, _attributes);
            data[.._attributes.Length].CopyTo(_attributes);
            _inodeBg.UpdateInode(_transaction, InodeInfo);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

#if NETSTANDARD2_0 || NETFRAMEWORK
        public int Read(Span<byte> buffer)
#else
        public override int Read(Span<byte> buffer)
#endif
        {
            if (buffer.IsEmpty)
                throw new ArgumentNullException(nameof(buffer));
            ThrowsIfNotValidRead();

            var maxLengthCanRead = Math.Min(buffer.Length, _length - _localBlockIndex * _blockSize - _localByteIndex);
            var byteIndex = 0;
            while (byteIndex < maxLengthCanRead)
            {
                var length = maxLengthCanRead - byteIndex;
                var (bg, bi) = GetDesireBlock(_localBlockIndex, ref length);
                if (length <= 0)
                    return 0;
                if (bg == null || bi < 0)
                    throw new SimFSException(ExceptionType.InconsistantDataValue, "bg == null || bi < 0 is not supposed to happen!");
                var read = bg.ReadContent(bi, _localByteIndex, buffer.Slice(byteIndex, length));
                if (read != length)
                    throw new SimFSException(ExceptionType.InconsistantDataValue, $"content size read: {read} is not match the requested length: {length}");
                UpdatePosition(read);
                byteIndex += read;
            }
            return byteIndex;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Write(buffer.AsSpan(offset, count));
        }

#if NETSTANDARD2_0 || NETFRAMEWORK
        public void Write(ReadOnlySpan<byte> buffer)
#else
        public override void Write(ReadOnlySpan<byte> buffer)
#endif
        {
            if (buffer.IsEmpty)
                return;
            ThrowsIfNotValidWrite();

            _isWritten = true;
            _transaction.FileBeforeChange(_inodeGlobalIndex, _length);
            var maxBufferSize = Math.Min(buffer.Length, _fsMan.Pooling.MaxBufferSize);
            var bufferWritten = 0;
            var pos = (int)Position;
            while (bufferWritten < buffer.Length)
            {
                var writeSize = Math.Min(buffer.Length - bufferWritten, maxBufferSize);
                var holder = _fsMan.Pooling.RentBuffer(out var span, writeSize, true);
                buffer.Slice(bufferWritten, writeSize).CopyTo(span);

                _transaction.FileWrite(_inodeGlobalIndex, pos + bufferWritten, holder);
                bufferWritten += writeSize;
            }
            UpdatePosition(buffer.Length);
            _length = Math.Max(pos + buffer.Length, _length);
        }

        private void WriteRawData(ReadOnlySpan<byte> buffer)
        {
            var byteIndex = 0;
            while (byteIndex < buffer.Length)
            {
                var length = buffer.Length - byteIndex;
                var (bg, bi) = GetDesireBlock(_localBlockIndex, ref length);
                bg.WriteRawData(bi, _localByteIndex, buffer.Slice(byteIndex, length));
                UpdatePosition(length);
                byteIndex += length;
            }
        }

        internal void RevertChanges(FileTransData ftd)
        {
            if (ftd.attributes.IsValid)
                ftd.attributes.Span.CopyTo(_attributes);
            _length = ftd.originLength;
        }

        internal void SaveChanges(Transaction transaction, FileTransData ftd)
        {
            if (_transaction != null && _transaction != transaction)
                throw new SimFSException(ExceptionType.TransactionMismatch);
            var lastTransaction = _transaction;
            _transaction ??= transaction;

            var lengthTotal = GetMaxLength(ftd.writes);
            if (lengthTotal > GetMaxFileSize(_blockPointers.Length, _blockSize))
                throw new SimFSException(ExceptionType.FileTooLarge);

            List<WriteOperation> defragRelocates = null;
            if (lengthTotal > ftd.originLength)
            {
                var bytesAllocated = GetBlocksAllocated(_blockPointers) * _blockSize;
                if (bytesAllocated < lengthTotal)
                {
                    var bytesToAllocate = lengthTotal - bytesAllocated;
                    defragRelocates = AllocateMoreBlocks(transaction, bytesToAllocate);
                    if (GetBlocksAllocated(_blockPointers) * _blockSize < lengthTotal)
                        throw new SimFSException(ExceptionType.UnableToAllocateMoreSpaces, "AllocateNewBlocks went wrong");
                }

                // preset length, this is important, if length is not pre allocated,
                // the data will be immediate overwrite with 0s after moving the `Position`
                _length = Math.Max(_length, lengthTotal);
            }

            List<WriteOperation> finalOps;
            if (defragRelocates != null && defragRelocates.Count > 0)
            {
                Position = defragRelocates[0].Location;
                foreach (var (_, buffer) in defragRelocates)
                {
                    WriteRawData(buffer);
                    buffer.Dispose();
                }
                defragRelocates.Clear();
                finalOps = defragRelocates;
            }
            else
                finalOps = _fsMan.Pooling.TransactionPooling.CompactWriteOpsPool.Get();

            CompactOperations(ftd.writes, finalOps);

            foreach (var (localPosition, buffer) in finalOps)
            {
                Position = localPosition;
                WriteRawData(buffer);
            }

            _fsMan.Pooling.TransactionPooling.CompactWriteOpsPool.Return(finalOps);

            _inodeBg.UpdateInode(transaction, InodeInfo);
            _transaction = lastTransaction;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static int GetMaxLength(SortedList<WriteOperation> ops)
            {
                var max = 0;
                foreach (var op in ops)
                {
                    var num = op.Location + op.Data.Length;
                    if (num > max)
                        max = num;
                }
                return max;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static int GetBlocksAllocated(BlockPointerData[] bpds)
            {
                var sum = 0;
                foreach (var bpd in bpds)
                {
                    sum += bpd.blockCount;
                }
                return sum;
            }
        }


        private void CompactOperations(SortedList<WriteOperation> inputList, List<WriteOperation> finalOps)
        {
            foreach (var (loc, data) in inputList)
            {

                if (loc < 0) // op is completely trimmed.
                    continue;
                if (finalOps.Count == 0)
                {
                    finalOps.Add(new WriteOperation(loc, data));
                    continue;
                }

                var (lastLoc, lastData) = finalOps[^1];
                var lastOpStart = lastLoc;
                var lastOpEnd = lastOpStart + lastData.Length;
                var opStart = loc;
                var opEnd = opStart + data.Length;
                if (opStart < lastOpEnd)
                {
                    if (opEnd > lastOpEnd)
                    {
                        var length = opStart - lastOpStart;
                        if (length > 0)
                        {
                            finalOps[^1] = new WriteOperation(lastOpStart, lastData[..length]);
                        }
                        else
                            finalOps.RemoveAt(finalOps.Count - 1);
                    }
                    else
                    {
                        var writeStart = opStart - lastOpStart;
                        data.Span.CopyTo(lastData.Span[writeStart..]);
                        continue; // op is merged into lastOp, no need to add to the list.
                    }
                }

                finalOps.Add(new WriteOperation(loc, data));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Goto(long position)
        {
            ThrowsIfNotValid();
            if (position > Length)
                SetLength(position);
            (_localBlockIndex, _localByteIndex) = GetLocation(position);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private (int blockIndex, int byteIndex) GetLocation(long position)
        {
            return ((int)(position / _blockSize),
                (int)(position % _blockSize));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdatePosition(int length)
        {
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length));

            _localByteIndex += length;
            if (_localByteIndex >= _blockSize)
            {
                _localBlockIndex += _localByteIndex / _blockSize;
                _localByteIndex %= _blockSize;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowsIfNotValid();
            return SeekCore(offset, origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.End => Length,
                SeekOrigin.Current => Position,
                _ => throw new NotSupportedException(),
            });
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private long SeekCore(long offset, long loc)
        {
            if (offset > Length)
                throw new ArgumentOutOfRangeException();
            var pos = loc + offset;
            if ((uint)pos > (uint)Length)
                throw new ArgumentOutOfRangeException();
            Position = pos;
            return pos;
        }

        public override void Flush()
        {
            ThrowsIfNotValidWrite();
            if (_transaction.Mode == TransactionMode.Immediate)
                _transaction.Commit();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void SetLength(long value)
        {
            ThrowsIfNotValidWrite();
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (value > _blockPointers.Length * byte.MaxValue * _blockSize)
                throw new SimFSException(ExceptionType.FileTooLarge);
            var oldLength = _length;
            var newLength = (int)value;
            if (newLength > oldLength)
            {
                var lengthChange = newLength - oldLength;
                var maxBufferSize = Math.Min(lengthChange, _fsMan.Pooling.MaxBufferSize);
                using var _ = _fsMan.Pooling.RentBuffer(out var buffer, maxBufferSize);
                buffer = buffer[..maxBufferSize];
                buffer.Clear();
                var bytesWritten = 0;
                while (bytesWritten < lengthChange)
                {
                    var bytesToWrite = lengthChange - bytesWritten;
                    bytesToWrite = Math.Min(bytesToWrite, maxBufferSize);
                    Write(buffer[..bytesToWrite]);
                    bytesWritten += bytesToWrite;
                }
            }
            else
                _length = newLength;
            _inodeBg.UpdateInode(_transaction, InodeInfo);
        }


        private (BlockGroup bg, int blockIndex) GetDesireBlock(int localBlockIndex, ref int length)
        {
            var accumulatedBlockCount = 0;
            for (var i = 0; i < _blockPointers.Length; i++)
            {
                var curIndex = localBlockIndex - accumulatedBlockCount;
                var bpd = _blockPointers[i];
                if (bpd.IsEmpty) // this will happen when the i - 1 block pointer was 100% full
                {
                    if (i == 0) // every inode has at least 1 non-empty block pointer, so it shouldn't be 0
                        throw new SimFSException(ExceptionType.InvalidInode, $"current inode is empty, index: {_inodeGlobalIndex}");
                }
                else if (localBlockIndex < bpd.blockCount + accumulatedBlockCount)
                {
                    length = Math.Min(length, (bpd.blockCount - curIndex) * _blockSize - _localByteIndex);
                    if (length == 0)
                        throw new SimFSException(ExceptionType.InternalError, "length should not be 0");
                    var (bgIndex, bi) = FSMan.GetLocalIndex(bpd.globalIndex, _blockSize);
                    var bg = GetInodeBlockGroup(bgIndex);
                    return (bg, bi + curIndex);
                }
                accumulatedBlockCount += bpd.blockCount;
            }
            throw new SimFSException(ExceptionType.InvalidFileStream, $"unable to fetch the right block");
        }

        private List<WriteOperation> AllocateMoreBlocks(Transaction transaction, int byteToAllocate)
        {
            List<WriteOperation> defragRelocates = null;
            var blocksToAllocate = 0;
            {
                var blocksAlreadyHas = _blockPointers.Sum(bpd => bpd.blockCount);
                var minBlocksToAllocate = SimUtil.Number.IntDivideCeil(byteToAllocate, _blockSize);
                blocksToAllocate = Math.Min(Math.Max(minBlocksToAllocate * 2, blocksAlreadyHas),
                    GetMaxBlocksCanAllocate(_blockPointers.Length) - blocksAlreadyHas);
                if (minBlocksToAllocate > blocksToAllocate)
                    throw new SimFSException(ExceptionType.UnableToAllocateMoreSpaces);
            }
            var blocksAllocated = 0;
            for (var i = 0; i < _blockPointers.Length; i++)
            {
                if (blocksAllocated >= blocksToAllocate)
                    break;
                var maxBlocksSinglePointer = Math.Min(blocksToAllocate - blocksAllocated, byte.MaxValue);
                var bpd = _blockPointers[i];
                if (bpd.IsEmpty)
                {
                    bpd = _fsMan.AllocateBlockNearInode(transaction, _inodeGlobalIndex, out var bg, maxBlocksSinglePointer);
                    _blockPointers[i] = bpd;
                    blocksAllocated += bpd.blockCount;
                }
                //only the last valid bpd can expand!
                else if (bpd.blockCount < byte.MaxValue && IsNextBlockPointerEmpty(_blockPointers, i))
                {
                    var (gi, bi) = FSMan.GetLocalIndex(bpd.globalIndex, _blockSize);
                    var bg = GetInodeBlockGroup(gi);
                    var actualBlocksExpanded = bg.ExpandBlockUsageAtBest(transaction, ref bpd, Math.Min(maxBlocksSinglePointer, byte.MaxValue - bpd.blockCount));
                    if (actualBlocksExpanded > 0)
                        _blockPointers[i] = bpd;
                    blocksAllocated += actualBlocksExpanded;
                }
            }
            if (blocksAllocated < blocksToAllocate)
            {
                blocksAllocated += SelfDefrag(transaction, blocksToAllocate - blocksAllocated, out defragRelocates);
                if (blocksAllocated < blocksToAllocate)
                    throw new SimFSException(ExceptionType.UnableToAllocateMoreSpaces, "cannot allocate more blocks");
            }
            return defragRelocates;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool IsNextBlockPointerEmpty(BlockPointerData[] blockPointers, int i)
            {
                if (i < 0 || i >= blockPointers.Length)
                    throw new ArgumentOutOfRangeException($"i: {i}");
                if (i == blockPointers.Length - 1)
                    return true;
                return blockPointers[i + 1].IsEmpty;
            }
        }

        private BlockGroup GetInodeBlockGroup(int bgIndex) => bgIndex == _inodeBg.GroupIndex ? _inodeBg : _fsMan.GetBlockGroup(bgIndex);

        private int SelfDefrag(Transaction transaction, int goalBlocksToAlloc, out List<WriteOperation> defragRelocations)
        {
            defragRelocations = null;
            var startPointerIndex = 0;
            var oldBlockCount = 0;
            for (var i = 0; i < _blockPointers.Length; i++)
            {
                if (_blockPointers[i].IsEmpty)
                    break;

                var count = _blockPointers[i].blockCount;
                if (count == byte.MaxValue && oldBlockCount == 0)
                {
                    startPointerIndex++;
                    continue;
                }
                oldBlockCount += count;
            }
            var maxBlockCount = (_blockPointers.Length - startPointerIndex) * byte.MaxValue;
            if (maxBlockCount == 0)
                return 0;
            var newBlockCount = Math.Max(oldBlockCount + goalBlocksToAlloc, oldBlockCount * 2);
            newBlockCount = Math.Min(newBlockCount, maxBlockCount);
            defragRelocations = _fsMan.Pooling.TransactionPooling.CompactWriteOpsPool.Get();
            var allocatedBlocks = 0;
            var defragCount = 0;
            Span<BlockPointerData> newPointers = stackalloc BlockPointerData[_blockPointers.Length];
            Position = Math.Min(Length, GetMaxFileSize(startPointerIndex, _blockSize));
            while (allocatedBlocks < newBlockCount)
            {
                var blocksToAlloc = Math.Min(newBlockCount - allocatedBlocks, byte.MaxValue);
                var totalLength = blocksToAlloc * _blockSize;
                var nbpd = _fsMan.AllocateBlockNearInode(transaction, _inodeGlobalIndex, out var bg, blocksToAlloc);
                var (gi, bi) = FSMan.GetLocalIndex(nbpd.globalIndex, _blockSize);
                var index = 0;
                var writeBi = _localBlockIndex;
                var writeBo = _localByteIndex;
                totalLength = Math.Min((int)Length - (int)Position, totalLength);
                if (totalLength > 0)
                {
                    var bufferSize = Math.Min(_fsMan.Pooling.MaxBufferSize, totalLength);
                    while (index < totalLength)
                    {
                        var size = Math.Min(bufferSize, totalLength - index);
                        var buffer = _fsMan.Pooling.RentBuffer(out var span, size, true);
                        var read = Read(span);
                        if (read != span.Length)
                            throw new SimFSException(ExceptionType.InconsistantDataValue, $"content size read: {read} is not match the requested length: {span.Length}");
                        defragRelocations.Add(new WriteOperation(writeBi * _blockSize + writeBo, buffer));

                        index += read;
                        writeBo += read;
                        if (writeBo > _blockSize)
                        {
                            writeBi += writeBo / _blockSize;
                            writeBo %= _blockSize;
                        }
                    }
                }
                newPointers[startPointerIndex + defragCount] = nbpd;
                defragCount++;
                allocatedBlocks += blocksToAlloc;
            }
            for (var i = startPointerIndex; i < _blockPointers.Length; i++)
            {
                var oldBpd = _blockPointers[i];
                if (!oldBpd.IsEmpty)
                {
                    var (bgIndex, _) = FSMan.GetLocalIndex(oldBpd.globalIndex, _blockSize);
                    var obg = GetInodeBlockGroup(bgIndex);
                    obg.FreeBlocks(transaction, oldBpd);
                }
                _blockPointers[i] = newPointers[i];
            }
            return newBlockCount - oldBlockCount;
        }

        protected override void Dispose(bool disposing)
        {
            ThrowsIfNotValid();
            if (_transaction != null && _transaction.Mode == TransactionMode.Temproary)
            {
                _transaction.Dispose();
            }
            _transaction = null;
            if (_fileAccess == FileAccess.ReadWrite)
                _sharingData.ExitWriteState(this);
            _fsMan.CloseFile(_inodeGlobalIndex, this, _fileAccess);
            _fsMan.Pooling.FileStreamPool.Return(this);
        }
    }
}
