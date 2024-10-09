using System;
using System.Collections.Generic;
using System.IO;

namespace SimFS
{
    internal class WriteBuffer
    {
        private class SectionComparer : IComparer<Section>
        {
            public static SectionComparer Default { get; } = new();
            public int Compare(Section x, Section y)
            {
                return (int)(x.writeLocation - y.writeLocation);
            }
        }

        public readonly struct Section
        {
            public Section(long writeLocation, Memory<byte> bytes)
            {
                this.writeLocation = writeLocation;
                this.bytes = bytes;
            }
            public readonly long writeLocation;
            public readonly Memory<byte> bytes;
        }

        public WriteBuffer(int bufferSize, Stream fs, ReadWriteLock rwLock)
        {
            if (bufferSize < 1024)
                throw new ArgumentOutOfRangeException(nameof(bufferSize) + " must greater than 1024");
            _buffer = new byte[bufferSize];
            _fs = fs ?? throw new ArgumentNullException(nameof(fs));
            _rwLock = rwLock ?? throw new ArgumentNullException(nameof(rwLock));
        }

        private readonly Memory<byte> _buffer;
        private readonly Stream _fs;
        private readonly ReadWriteLock _rwLock;

        private readonly SortedList<Section> _sections = new(SectionComparer.Default);
        private int _nextIndex;

        public int Size => _buffer.Length;

        public Section Rent(long writeLocation, int length)
        {
            if (_rwLock.State == ReadWriteState.Writing)
                throw new SimFSException(ExceptionType.InvalidOperation, "currently is writing");
            if (writeLocation < 0)
                throw new ArgumentOutOfRangeException(nameof(writeLocation));
            if (length > _buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(length) + $"cannot greater than buffer size: {_buffer.Length}");
            if (_nextIndex + length >= _buffer.Length)
                Flush();
            Memory<byte> buffer = _buffer.Slice(_nextIndex, length);
            _nextIndex += length;
            var section = new Section(writeLocation, buffer);
            var index = _sections.IndexOf(section, false);
            if (index >= 0 && _sections[index].bytes.Length <= length)
                _sections.RemoveAt(index);
            _sections.Add(section);
            return section;
        }

        public void Flush()
        {
            if (_nextIndex == 0)
                return;
            using var _ = ReadWriteLocker.BeginWrite(_rwLock);
            try
            {
                for (var i = 0; i < _sections.Count; i++)
                {
                    var section = _sections[i];
                    if (section.bytes.IsEmpty)
                        continue;
                    if (_fs.Position != section.writeLocation)
                        _fs.Position = section.writeLocation;
                    _fs.Write(section.bytes.Span);
                }
                _sections.Clear();
                _fs.Flush();
            }
            finally
            {
                _nextIndex = 0;
            }
        }
    }
}
