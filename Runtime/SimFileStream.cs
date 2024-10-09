using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;

namespace SimFS
{
    public class SimFileStream : Stream
    {
        internal SimFileStream()
        {
            InPool();
        }

        internal void LoadFileStream(FSMan fsMan, InodeInfo inodeInfo, BlockGroup bg = null)
        {
            inodeInfo.ThrowsIfNotValid();
            var (inodeGlobalIndex, inodeData) = inodeInfo;
            _fsMan = fsMan ?? throw new ArgumentNullException(nameof(fsMan));
            _fsMan.TryOpenFile(inodeGlobalIndex);
            _blockSize = fsMan.Head.BlockSize;
            _inodeGlobalIndex = inodeGlobalIndex;
            _usage = inodeData.usage;
            _length = inodeData.length;
            _blockPointers = inodeData.blockPointers;
            _attributes = inodeData.attributes;
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
        }

        internal void InPool()
        {
            _usage = InodeUsage.Unused;
            _blockSize = 0;
            _fsMan = null;
            _inodeBg = null;
            _localBlockIndex = -1;
            _localByteIndex = -1;
        }

        private InodeUsage _usage;
        private int _length;
        private BlockPointerData[] _blockPointers;
        private byte[] _attributes;
        private ushort _blockSize;
        private FSMan _fsMan;
        private int _inodeGlobalIndex;
        private BlockGroup _inodeBg;
        private int _localBlockIndex;
        private int _localByteIndex;

        public override bool CanWrite => true;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override long Length => _length;
        public override long Position { get => _localBlockIndex * _blockSize + _localByteIndex; set => Goto(value); }

        internal InodeInfo InodeInfo => new(_inodeGlobalIndex, new(_length, _usage, _attributes, _blockPointers));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ThrowsIfNotValid()
        {
            if (_usage == InodeUsage.Unused || _blockSize == 0 || _fsMan == null || _localBlockIndex < 0 || _localBlockIndex < 0)
                throw new SimFSException(ExceptionType.InvalidFileStream);
        }

        public void ReadAttributes(Span<byte> buffer)
        {
            ThrowsIfNotValid();
            if (buffer.Length < _attributes.Length)
                throw new ArgumentException("buffer size is too small");
            _attributes.CopyTo(buffer);
        }

        public void WriteAttributes(ReadOnlySpan<byte> buffer)
        {
            ThrowsIfNotValid();
            if (buffer.Length < _attributes.Length)
                throw new ArgumentException("buffer size is too small");
            buffer[.._attributes.Length].CopyTo(_attributes);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
                throw new ArgumentNullException(nameof(buffer));
            ThrowsIfNotValid();
            _fsMan.WriteBuffer.Flush();

            var maxLengthCanRead = Math.Min(buffer.Length, _length - _localBlockIndex * _blockSize - _localByteIndex);
            var byteIndex = 0;
            while (byteIndex < maxLengthCanRead)
            {
                var length = maxLengthCanRead - byteIndex;
                var (bg, bi) = GetDesireBlock(_localBlockIndex, ref length, false);
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

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.IsEmpty)
                throw new ArgumentNullException(nameof(buffer));
            ThrowsIfNotValid();

            var totalDataSize = buffer.Length + _localByteIndex + _localBlockIndex;
            var totalBlockLength = (totalDataSize + _blockSize - 1) / _blockSize;
            if (totalBlockLength > _blockPointers.Length * 255)
                throw new SimFSException(ExceptionType.FileTooLarge);

            var writeBuffer = _fsMan.WriteBuffer;
            var writeBufSize = Math.Min(buffer.Length, writeBuffer.Size);

            var byteIndex = 0;
            while (byteIndex < buffer.Length)
            {
                var length = Math.Min(writeBufSize, buffer.Length - byteIndex);
                var (bg, bi) = GetDesireBlock(_localBlockIndex, ref length, true);
                bg.WriteContent(bi, _localByteIndex, buffer.Slice(byteIndex, length));
                UpdatePosition(length);
                byteIndex += length;
            }
            _length = Math.Max(_length, _localBlockIndex * _blockSize + _localByteIndex);
            _inodeBg.UpdateInode(InodeInfo);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Goto(long position)
        {
            ThrowsIfNotValid();
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
            ThrowsIfNotValid();

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void Flush()
        {
            ThrowsIfNotValid();
            _fsMan.Flush();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void SetLength(long value)
        {
            ThrowsIfNotValid();
            if (value > int.MaxValue || value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            _length = (int)value;
        }


        private (BlockGroup bg, int blockIndex) GetDesireBlock(int localBlockIndex, ref int length, bool allocateWhenNeeded)
        {
            // the block count needs to minus _localByteIndex, because the last block is not fully filled
            var blockCount = (length + _blockSize + _localByteIndex - 1) / _blockSize;
        Retry:
            var accumulatedBlockCount = 0;
            for (var i = 0; i < _blockPointers.Length; i++)
            {
                var curIndex = localBlockIndex - accumulatedBlockCount;
                var bpd = _blockPointers[i];
                if (bpd.IsEmpty) // this will happen when the last block pointer was 100% full
                {
                    if (i == 0) // every inode has at least 1 non empty block pointer, so i shouldn't be 0
                        throw new SimFSException(ExceptionType.InternalError, $"current inode is empty, index: {_inodeGlobalIndex}");
                    if (AllocateNewBlocks(ref bpd, i, curIndex, blockCount))
                    {
                        // because this file just self defragmented, all the block pointers all changed, so we must start the for loop again
                        // without recursively calling the same method using the evil goto statement.
                        goto Retry;
                    }
                }
                if (localBlockIndex < bpd.blockCount + accumulatedBlockCount)
                {
                    BlockGroup bg;
                    if (bpd.blockCount - curIndex < blockCount && allocateWhenNeeded) //current contiguous block range doesn't fit the total block count
                    {
                        if (AllocateNewBlocks(ref bpd, i, curIndex, blockCount))
                        {
                            goto Retry;
                        }
                    }
                    length = Math.Min(length, (bpd.blockCount - curIndex) * _blockSize - _localByteIndex);
                    var (bgIndex, bi) = FSMan.GetLocalIndex(bpd.globalIndex, _blockSize);
                    bg = bgIndex == _inodeBg.GroupIndex ? _inodeBg : _fsMan.GetBlockGroup(bgIndex);
                    return (bg, bi + curIndex);
                }
                accumulatedBlockCount += bpd.blockCount;
            }
            throw new InvalidOperationException(" the code should never walk to here!");
        }

        private bool AllocateNewBlocks(ref BlockPointerData bpd, int pointerIndex, int curIndex, int blockCount)
        {
            var needsRecalculatePosition = false;
            var nextBlockCount = blockCount - bpd.blockCount + curIndex;
            var (bgIndex, _) = FSMan.GetLocalIndex(bpd.globalIndex, _blockSize);
            var bg = bgIndex == _inodeBg.GroupIndex ? _inodeBg : _fsMan.GetBlockGroup(bgIndex);
            var curBlockCount = 0;
            for (var i = 0; i < _blockPointers.Length; i++)
            {
                if (_blockPointers[i].IsEmpty)
                    break;
                curBlockCount += _blockPointers[i].blockCount;
            }
            var expandCount = Math.Min(byte.MaxValue, SimUtil.Number.NextMultipleOf(nextBlockCount, curBlockCount));
            // first try expand the block pointer size
            if (bg.ExpandBlockUsage(ref bpd, expandCount))
            {
                // reassign the block pointer if success
                _blockPointers[pointerIndex] = bpd;
            }
            else if (HasFreeBlockPointers()) // then check is there space to allocate new block pointer
            {
                var newBlockCount = Math.Min(byte.MaxValue, SimUtil.Number.NextMultipleOf(nextBlockCount, curBlockCount * 2));
                var nbpd = _fsMan.AllocateBlockNearInode(_inodeGlobalIndex, out _, newBlockCount);
                if (pointerIndex == AssignBlock(nbpd))
                    bpd = nbpd; // this happens when current bpd is empty
            }
            else
            {
                // finally compact read out all the data and write it to new and contiguous location,
                // and assign this location to a single block pointer(hopefully, because the total block count may over 255)
                SelfDefrag();
                needsRecalculatePosition = true;
            }
            _inodeBg.UpdateInode(InodeInfo);
            return needsRecalculatePosition;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool HasFreeBlockPointers()
        {
            foreach (var b in _blockPointers)
            {
                if (b.IsEmpty)
                    return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int AssignBlock(BlockPointerData bpd)
        {
            for (var i = 0; i < _blockPointers.Length; i++)
            {
                if (_blockPointers[i].IsEmpty)
                {
                    _blockPointers[i] = bpd;
                    return i;
                }
            }
            throw new SimFSException(ExceptionType.InvalidOperation, "blockpointers are full");
        }

        private void SelfDefrag()
        {
            var defragIndex = 0;
            var newBlockCount = 0;
            for (var i = 0; i < _blockPointers.Length; i++)
            {
                if (_blockPointers[i].IsEmpty)
                    break;
                var count = _blockPointers[i].blockCount;
                if (count == byte.MaxValue)
                {
                    defragIndex++;
                    continue;
                }
                newBlockCount += count;
            }
            var maxBlockCount = (_blockPointers.Length - defragIndex) * byte.MaxValue;
            if (newBlockCount > maxBlockCount)
                throw new SimFSException(ExceptionType.FileTooLarge);
            newBlockCount = Math.Min(newBlockCount * 2, maxBlockCount);
            using var bufferHolder = _fsMan.Pooling.RentBuffer(out var buffer, (int)Length);
            var allocatedBlocks = 0;
            while (allocatedBlocks < newBlockCount)
            {
                var pointerSize = Math.Min(newBlockCount, byte.MaxValue);
                var totalLength = pointerSize * _blockSize;
                var nbpd = _fsMan.AllocateBlockNearInode(_inodeGlobalIndex, out var bg, pointerSize);
                var (_, bi) = FSMan.GetLocalIndex(nbpd.globalIndex, _blockSize);
                Position = (defragIndex * byte.MaxValue + allocatedBlocks) * _blockSize;
                var index = 0;
                var writeBi = 0;
                var writeBo = 0;
                totalLength = Math.Min((int)(Length - Position), totalLength);
                while (index < totalLength)
                {
                    var read = Read(buffer);
                    bg.WriteContent(bi + writeBi, writeBo, buffer[..read]);
                    index += read;
                    writeBo += read;
                    if (writeBo > _blockSize)
                    {
                        writeBi += writeBo / _blockSize;
                        writeBo %= _blockSize;
                    }
                }
                var obg = bg;
                for (var i = defragIndex; i < _blockPointers.Length; i++)
                {
                    var oldBpd = _blockPointers[i];
                    if (oldBpd.IsEmpty)
                        break;
                    var (bgIndex, _) = FSMan.GetLocalIndex(oldBpd.globalIndex, _blockSize);
                    obg = bgIndex == obg.GroupIndex ? obg : _fsMan.GetBlockGroup(bgIndex);
                    obg.FreeBlocks(oldBpd);
                    _blockPointers[i] = default;
                }
                AssignBlock(nbpd);
                defragIndex++;
                allocatedBlocks += pointerSize;
            }
        }

        protected override void Dispose(bool disposing)
        {
            ThrowsIfNotValid();
            _fsMan.Flush();
            _fsMan.CloseFile(_inodeGlobalIndex);
            _fsMan.Pooling.FileStreamPool.Return(this);
        }
    }
}
