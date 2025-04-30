/*
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SimFS
{
    public class TransactionLocalStorageSettings
    {
        public int MaxBufferSizeMult { get; set; } = 8;
        public int MaxDirectoryEntriesMult { get; set; } = 5;
        public int MaxBlockGroupMetas { get; set; } = 32;
    }

    public class TransactionLocalStorage
    {
        [StructLayout(LayoutKind.Explicit)]
        private struct RecordHeader
        {
            public const ushort SIGNATURE_WRITE_OPERATION = 749;
            public const ushort SIGNATURE_DIRECTORY_CHANGE = 750;
            public const ushort SIGNATURE_BLOCKGROUP_CHANGE = 750;
            public static readonly int MemSize = Marshal.SizeOf<RecordHeader>();
            [FieldOffset(0)]
            public ushort signature;
            [FieldOffset(2)]
            public long size;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct WriteOperationHeader
        {
            public static readonly int MemSize = Marshal.SizeOf<WriteOperationHeader>();
            [FieldOffset(0)]
            public long location;
            [FieldOffset(8)]
            public int length;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct DirectoryChangeHeader
        {
            public static readonly int MemSize = Marshal.SizeOf<DirectoryChangeHeader>();
            [FieldOffset(0)]
            public int globalIndex;
            [FieldOffset(4)]
            public int entryCount;
            [FieldOffset(8)]
            public int dataCount;
        }

        private struct DirectoryChange
        {
            private static readonly int MemSizeBase = 11;
            public static ushort EstimateSize(ReadOnlyMemory<char> name) => (ushort)(MemSizeBase + name.Length);
            public int entryIndex;
            public DirectoryEntryData entryData;
            public char[] name;
        }


        public TransactionLocalStorage(string fileName)
        {
            _localFs = File.Open(fileName, FileMode.OpenOrCreate);
            _localFs.Position = 0;
            _localFs.SetLength(0);
        }

        private readonly FileStream _localFs;

        private bool _operating = false;

        internal long SaveBlockGroupMeta(Dictionary<int, BlockGroupTransData> metas, ushort blockSize, Pooling pooling)
        {
            if (_operating)
                throw new SimFSException(ExceptionType.TransactionIsAlreadyWriting);

            _operating = true;
            var startLocation = _localFs.Position;
            try
            {
                using var bufferHolder = pooling.RentBuffer(out var buffer, blockSize * 2);

                var header = new RecordHeader { signature = RecordHeader.SIGNATURE_BLOCKGROUP_CHANGE, size = metas.Count };

            }
            finally
            {
                _operating = false;
            }
            return startLocation;
        }

        internal long SaveDirectoryChanges(Dictionary<int, DirectoryTransData> dirChanges, Pooling pooling)
        {
            if (_operating)
                throw new SimFSException(ExceptionType.TransactionIsAlreadyWriting);

            _operating = true;
            var startLocation = _localFs.Position;
            try
            {
                using var bufferHolder = pooling.RentBuffer(out var buffer);

                var header = new RecordHeader { signature = RecordHeader.SIGNATURE_DIRECTORY_CHANGE, size = dirChanges.Count };
                var bufferSection = buffer;
                MemoryMarshal.TryWrite(bufferSection[..RecordHeader.MemSize], ref header);
                bufferSection = buffer[RecordHeader.MemSize..];
                foreach (var (globalIndex, dtd) in dirChanges)
                {
                    var sectionStartLocation = _localFs.Position;
                    var dcHeader = new DirectoryChangeHeader { globalIndex = globalIndex, entryCount = dtd.entryCount, dataCount = dtd.DataCount };
                    bufferSection = TryWriteBuffer(buffer, bufferSection, DirectoryChangeHeader.MemSize);
                    MemoryMarshal.TryWrite(bufferSection[..DirectoryChangeHeader.MemSize], ref dcHeader);
                    foreach (var (entryIndex, tuple) in dtd.entryData)
                    {
                        var entryData = tuple.Item1;
                        var name = tuple.Item2;
                        var nameLength = Encoding.UTF8.GetByteCount(name.Span);
                        var totalSize = (ushort)(4 + entryData.entryLength);
                        bufferSection = TryWriteBuffer(buffer, bufferSection, totalSize);
                        BitConverter.TryWriteBytes(bufferSection[..4], entryIndex);
                        bufferSection = bufferSection[4..];
                        MemoryMarshal.TryWrite(bufferSection, ref entryData);
                        DirectoryEntryData.WriteToBuffer(bufferSection, entryData, name);
                    }
                }
                if (bufferSection.Length < buffer.Length)
                    _localFs.Write(buffer[..(buffer.Length - bufferSection.Length)]);
            }
            finally
            {
                _operating = false;
            }

            Span<byte> TryWriteBuffer(Span<byte> buffer, Span<byte> section, int length)
            {
                if (section.Length < length)
                {
                    _localFs.Write(buffer[..(buffer.Length - section.Length)]);
                    return buffer;
                }
                return section;
            }

            return startLocation;
        }

        internal void LoadDirectoryChanges(long position, Dictionary<int, DirectoryTransData> changes, Pooling pooling)
        {
            if (_operating)
                throw new SimFSException(ExceptionType.TransactionIsAlreadyWriting);
            _operating = true;
            try
            {
                using var bufferHolder = pooling.RentBuffer(out var buffer);
                _localFs.Position = position;
                var headBufferSpan = buffer[..RecordHeader.MemSize];
                _localFs.Read(headBufferSpan);
                MemoryMarshal.TryRead(headBufferSpan, out RecordHeader header);
                if (header.signature != RecordHeader.SIGNATURE_DIRECTORY_CHANGE)
                    throw new SimFSException(ExceptionType.TransactionErrorReadLocation, $"position: {position}");
                var dirCount = (int)header.size;
                for (var i = 0; i < dirCount; i++)
                {
                    var dtd = new DirectoryTransData();
                    var entries = new Dictionary<int, (DirectoryEntryData, ReadOnlyMemory<char>)>();
                    dtd.entryData = entries;
                    headBufferSpan = buffer[..DirectoryChangeHeader.MemSize];
                    _localFs.Read(headBufferSpan);
                    MemoryMarshal.TryRead(headBufferSpan, out DirectoryChangeHeader dch);
                    var entryCount = dch.entryCount;
                    var length = _localFs.Length;
                    var read = 0;
                    var bytesLeft = 0;
                    while (read < length && entries.Count < entryCount)
                    {
                        var loopRead = _localFs.Read(buffer[bytesLeft..]);
                        read += loopRead;
                        if (read <= 0)
                            break;
                        var readBuffer = buffer[..(loopRead + bytesLeft)];
                        var dirRead = 0;
                        do
                        {
                            if (readBuffer.Length <= 5)
                                break;
                            var entryIndex = BitConverter.ToInt32(readBuffer[..4]);
                            readBuffer = readBuffer[4..];
                            dirRead = DirectoryEntryData.ReadFromBuffer(readBuffer, out var entry, out var name);
                            if (dirRead > 0)
                            {
                                entries[entryIndex] = (entry, name.AsMemory());
                                readBuffer = readBuffer[dirRead..];
                                if (entries.Count == entryCount)
                                    break;
                            }
                        } while (dirRead > 0);
                        bytesLeft = readBuffer.Length;
                        readBuffer.CopyTo(buffer);
                    }
                    changes.Add(dch.globalIndex, dtd);
                }
            }
            finally
            {
                _operating = false;
            }
        }

        internal List<BufferProvider> LoadWriteOperations(long position, List<WriteOperation> writeOperations, Pooling pooling)
        {
            if (_operating)
                throw new SimFSException(ExceptionType.TransactionIsAlreadyWriting);

            _operating = true;
            try
            {
                using var bufferHolder = pooling.RentBuffer(out var headerBufferArray, 16);
                var buffers = pooling.BufferListPool.Get();
                var buffer = pooling.BufferPool.Get();
                _localFs.Position = position;
                var headerBufferSpan = headerBufferArray[..RecordHeader.MemSize];
                _localFs.Read(headerBufferSpan);
                MemoryMarshal.TryRead(headerBufferSpan, out RecordHeader header);
                if (header.signature != RecordHeader.SIGNATURE_WRITE_OPERATION)
                    throw new SimFSException(ExceptionType.TransactionErrorReadLocation, $"position: {position}");
                var length = 0;
                headerBufferSpan = headerBufferArray[..WriteOperationHeader.MemSize];
                while (length < header.size)
                {
                    _localFs.Read(headerBufferSpan);
                    MemoryMarshal.TryRead(headerBufferSpan, out WriteOperationHeader opEntryHeader);
                    if (!buffer.TryRent(opEntryHeader.length, out var writeBuffer))
                    {
                        buffers.Add(buffer);
                        buffer = pooling.BufferPool.Get();
                        if (!buffer.TryRent(opEntryHeader.length, out writeBuffer))
                            throw new SimFSException(ExceptionType.TransactionUnableToRentBuffer);
                    }
                    _localFs.Read(writeBuffer.Span);
                    writeOperations.Add(new WriteOperation(opEntryHeader.location, writeBuffer));
                }
                if (writeOperations.Count > 0)
                    buffers.Add(buffer);
                return buffers;
            }
            finally
            {
                _operating = false;
            }
        }

        internal void ReturnReadBuffers(List<BufferProvider> buffers, Pooling pooling)
        {
            foreach (var buffer in buffers)
            {
                pooling.BufferPool.Return(buffer);
            }
            pooling.BufferListPool.Return(buffers);
        }

        internal long SaveWriteOperations(List<WriteOperation> writeOps, Pooling pooling)
        {
            if (_operating)
                throw new SimFSException(ExceptionType.TransactionIsAlreadyWriting);

            _operating = true;
            var startLocation = _localFs.Position;
            try
            {
                using var bufferHodler = pooling.RentBuffer(out var buffer, 16);
                var header = new RecordHeader { signature = RecordHeader.SIGNATURE_WRITE_OPERATION, size = 0 };
                Span<byte> headerBuffer;
                _localFs.Position += RecordHeader.MemSize; //header struct size is 10
                var size = 0;
                headerBuffer = buffer[..WriteOperationHeader.MemSize];
                foreach (var op in writeOps)
                {
                    var mem = op.Data.Value;
                    var entry = new WriteOperationHeader { location = op.Location, length = mem.Length };
                    //var pos = op.Location;
                    //var len = op.Data.Value.Length;
                    MemoryMarshal.TryWrite(headerBuffer, ref entry);
                    _localFs.Write(headerBuffer);
                    _localFs.Write(mem.Span);
                    size += entry.length + headerBuffer.Length; // opHeader size is 12
                }
                header.size = size;
                _localFs.Position = startLocation;
                headerBuffer = buffer[..RecordHeader.MemSize];
                MemoryMarshal.TryWrite(headerBuffer, ref header);
                _localFs.Position += size;
            }
            finally
            {
                _operating = false;
            }
            return startLocation;
        }
    }
}
*/
