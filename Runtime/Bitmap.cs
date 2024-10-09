using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SimFS
{
    internal class Bitmap
    {
        private class IndexComparer : IComparer<(int, List<int>)>
        {
            public static IndexComparer Default { get; } = new();
            public int Compare((int, List<int>) x, (int, List<int>) y)
            {
                return x.Item1 - y.Item1;
            }
        }

        private readonly struct FragmentData
        {
            public FragmentData(int length, List<int> positions)
            {
                this.length = length;
                this.positions = positions;
            }

            public readonly int length;
            public readonly List<int> positions;

            public void Deconstruct(out int length, out List<int> positions)
            {
                length = this.length;
                positions = this.positions;
            }
        }


        public Bitmap(ReadOnlySpan<byte> bitmap, IObjectPool<List<int>> pool)
        {
            if (bitmap == null)
                throw new ArgumentNullException(nameof(bitmap));
            if (bitmap.Length == 0)
                throw new ArgumentException(nameof(bitmap) + " size is 0");
            _fragments.SetCallbacks((s, l) => RemoveLengthIndex(l, s), (s, l) => AddLengthIndex(l, s));
            _bitmap = new byte[bitmap.Length];
            bitmap.CopyTo(_bitmap);
            Initialize(pool);

        }

        public Bitmap(int byteLength, IObjectPool<List<int>> pool)
        {
            _fragments.SetCallbacks((s, l) => RemoveLengthIndex(l, s), (s, l) => AddLengthIndex(l, s));
            _bitmap = new byte[byteLength];
            Initialize(pool);
        }

        private readonly byte[] _bitmap;
        private readonly RangeList _fragments = new();
        private readonly SortedList<(int pos, List<int> indices)> _fragIndicesByLen = new(IndexComparer.Default);
        private int _firstNonFragIndex;
        private int _freeBits;
        private IObjectPool<List<int>> _intListPool;

        public int RawLength => _bitmap.Length;
        public int Size => _bitmap.Length * 8;
        public byte[] GetBytes() => _bitmap;
        public int FreeBits => _freeBits;

        private void Initialize(IObjectPool<List<int>> pool)
        {
            _intListPool = pool;
            FindFirstNonFragmentIndex();
            FindFragments();
            CalculateFreeBits();
        }

        public void ReInitialize(ReadOnlySpan<byte> bitmap, IObjectPool<List<int>> pool)
        {
            bitmap.CopyTo(_bitmap);
            _fragments.Clear();
            _fragIndicesByLen.Clear();
            Initialize(pool);
        }

        public void Clear()
        {
            _bitmap.AsSpan().Clear();
            _fragments.Clear();
            foreach (var (_, indices) in _fragIndicesByLen)
            {
                _intListPool.Return(indices);
            }
            _fragIndicesByLen.Clear();
            Initialize(null);
        }

        private void FindFirstNonFragmentIndex()
        {
            _firstNonFragIndex = Size;
            for (int i = _bitmap.Length - 1; i >= 0; i--)
            {
                if (_bitmap[i] != 0)
                {
                    int endBitIndex = SimUtil.Number.Log2(_bitmap[i]);
                    _firstNonFragIndex = Combine(i, endBitIndex) + 1;
                    return;
                }
            }
            _firstNonFragIndex = 0;
        }

        private void FindFragments()
        {
            int si = -1, sj = -1;

            for (int i = 0; i <= _bitmap.Length; i++)
            {
                var baseIndex = i * 8;
                if (baseIndex >= _firstNonFragIndex)
                    return;
                var cur = _bitmap[i];

                if (cur == 255)
                {
                    if (si >= 0)
                    {
                        _fragments.AddRange(Combine(si, sj), Combine(i - si, 0 - sj));
                        sj = -1;
                        si = -1;
                    }
                    continue;
                }

                if (si < 0)
                    si = i;

                if (cur == 0) continue;

                for (var j = 0; j < 8; j++)
                {
                    var bit = 1 << j;
                    if (sj < 0 && (cur & bit) == 0)
                    {
                        sj = j;
                        if (si < 0)
                            si = i;
                    }
                    else if ((cur & bit) > 0 && sj >= 0)
                    {
                        _fragments.AddRange(Combine(si, sj), Combine(i - si, j - sj));
                        sj = -1;
                        si = -1;
                    }
                    if (baseIndex + j >= _firstNonFragIndex)
                        return;
                }
            }
        }
        private void CalculateFreeBits()
        {
            var val = _bitmap.Length * 8 - _firstNonFragIndex;
            foreach (var (position, length) in _fragments)
            {
                val += length;
            }
            _freeBits = val;
        }

        private (int byteIndex, byte bitOffset) ReadIndex(int index)
        {
            if (index < 0 || index >= _bitmap.Length * 8)
                throw new ArgumentOutOfRangeException(nameof(index));
            int byteIndex = index / 8;
            byte bitOffset = (byte)(index % 8);
            return (byteIndex, bitOffset);
        }

        // cannot use unsigned type here, the offset can be negative
        private int Combine(int byteIndex, int bitOffset)
        {
            return byteIndex * 8 + bitOffset;
        }

        public bool Check(int byteIndex, byte bitOffset)
        {
            var b = _bitmap[byteIndex];
            var bit = 1 << bitOffset;
            return (b & bit) > 0;
        }

        public bool Check(int index)
        {
            var (bi, bo) = ReadIndex(index);
            return Check(bi, bo);
        }

        public bool RangeCheck(int index, int count, bool flag)
        {
            var (bi, bo) = ReadIndex(index);
            return RangeCheck(bi, bo, count, flag);
        }

        public bool RangeCheck(int byteIndex, byte bitOffset, int count, bool flag)
        {
            var bo = bitOffset;
            for (var i = 0; i < count; i++)
            {
                if (Check(byteIndex, bo++) != flag)
                    return false;
                if (bo >= 8)
                {
                    byteIndex++;
                    bo = 0;
                }
            }
            return true;
        }

        public int Allocate(int length)
        {
            (int, List<int>) searchItem = (length, null);
            var index = _fragIndicesByLen.IndexOf(searchItem, false);
            if (index < 0)
                index = ~index;
            if (index < _fragIndicesByLen.Count)
            {
                var (_, indices) = _fragIndicesByLen[index];
                var bitIndex = indices[^1];
                var result = _fragments.RemoveRange(bitIndex, length);
                if (result != RangeList.RemoveResult.Removed && result != RangeList.RemoveResult.Shrinked)
                    throw new SimFSException(ExceptionType.BitmapMessedUp, $"_fragments is desynced");
                Update(bitIndex, length, true);
                return bitIndex;
            }
            else if (_firstNonFragIndex + length < Size)
            {
                var bitIndex = _firstNonFragIndex;
                _firstNonFragIndex += length;
                Update(bitIndex, length, true);
                return bitIndex;
            }
            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AddLengthIndex(int length, int bitIndex)
        {
            var lengthIndex = _fragIndicesByLen.IndexOf((length, null), false);
            if (lengthIndex < 0)
            {
                var list = _intListPool.Get();
                list.Add(bitIndex);
                _fragIndicesByLen.Add((length, list));
            }
            else
            {
                var (_, indices) = _fragIndicesByLen[lengthIndex];
                indices.Add(bitIndex);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RemoveLengthIndex(int length, int fragPos)
        {
            var lengthIndex = _fragIndicesByLen.IndexOf((length, null), false);
            if (lengthIndex < 0)
                throw new SimFSException(ExceptionType.BitmapMessedUp, $"_fragIndicesByLen is desynced");
            var (_, indices) = _fragIndicesByLen[lengthIndex];
            indices.Remove(fragPos);
            if (indices.Count <= 0)
            {
                _fragIndicesByLen.RemoveAt(lengthIndex);
                _intListPool.Return(indices);
            }
        }

        public bool ExpandAllocation(int blockIndex, int blockCount)
        {
            if (blockIndex + blockCount >= Size)
                return false;

            if (!RangeCheck(blockIndex, blockCount, false))
                return false;
            if (blockIndex + blockCount > _firstNonFragIndex)
                _firstNonFragIndex = blockIndex + blockCount;

            var searchIndex = _fragments.IndexOf(blockIndex);
            if (searchIndex < 0)
                searchIndex = ~searchIndex + 1;
            if (searchIndex < _fragments.Count)
            {
                var newIndex = blockIndex + blockCount;
                var (bitIndex, length) = _fragments[searchIndex];
                if (bitIndex < newIndex)
                {
                    var newLength = length - blockCount;
                    if (newLength < 0)
                        throw new SimFSException(ExceptionType.BitmapMessedUp, $"fragment over shrinked");
                    var result = _fragments.RemoveRange(bitIndex, newIndex - bitIndex);
                    if (result != RangeList.RemoveResult.Removed && result != RangeList.RemoveResult.Shrinked)
                        throw new SimFSException(ExceptionType.BitmapMessedUp, $"expand error encountered");
                }
            }
            Update(blockIndex, blockCount, true);
            return true;
        }

        public void Free(int position, int length)
        {
            if (position >= _firstNonFragIndex)
                throw new ArgumentOutOfRangeException($"position is greater than {nameof(_firstNonFragIndex)}");
            _fragments.AddRange(position, length, ref _firstNonFragIndex);
            Update(position, length, false);
        }

        private void Update(int byteIndex, byte bitOffset, int length, bool flag)
        {
            var freeVal = flag ? -1 : 1;
            for (var i = 0; i < length; i++)
            {
                var bo = bitOffset + i;
                var bi = byteIndex + bo / 8;
                var bit = 1 << (bo % 8);
                var byteVal = _bitmap[bi];
                byte tarVal = flag ? (byte)(_bitmap[bi] | bit) : (byte)(_bitmap[bi] & ~bit);
                if (byteVal == tarVal)
                    throw new SimFSException(ExceptionType.WrongBit, $"the bit at: {bi * 8 + bo} is already been set to {(flag ? 1 : 0)}");
                _bitmap[bi] = tarVal;
            }
            _freeBits += freeVal * length;
        }

        private void Update(int index, int length, bool flag)
        {
            var (bi, bo) = ReadIndex(index);
            Update(bi, bo, length, flag);
        }

        public override string ToString()
        {
            var str = "";
            for (var i = 0; i < Size; i++)
            {
                if (i > 0 && i % 16 == 0)
                    str += "\n";
                str += Check(i) ? "*" : "-";
            }
            return str;
        }

        public string FragmentsToString()
        {
            var chars = new char[Size];
            Array.Fill(chars, '*');
            foreach (var (pos, len) in _fragments)
            {
                for (var i = pos; i < len; i++)
                {
                    chars[pos + i] = '+';
                }
            }
            for (var i = _firstNonFragIndex; i < Size; i++)
            {
                chars[i] = '+';
            }
            var list = new List<char>(chars);
            var x = 0;
            var count = 0;
            while (x++ < list.Count)
            {
                count++;
                if (count == 16 && x < list.Count)
                {
                    list.Insert(x, '\n');
                    count = -1;
                }
            }
            return new string(list.ToArray()) + "\nfnfi: " + _firstNonFragIndex;
        }
    }
}
