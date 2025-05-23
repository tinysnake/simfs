using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace SimFS
{
    internal static partial class SimUtil
    {
        public static class Number
        {
            private static readonly int[] _tab32 = new[]
            {
                00, 09, 01, 10, 13, 21, 02, 29,
                11, 14, 16, 18, 22, 25, 03, 30,
                08, 12, 20, 28, 15, 17, 24, 07,
                19, 27, 23, 06, 26, 05, 04, 31
            };

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static uint NextPowerOf2(uint v)
            {
                v--;
                v |= v >> 1;
                v |= v >> 2;
                v |= v >> 4;
                v |= v >> 8;
                v |= v >> 16;
                v++;
                return v;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static int Log2(int v)
            {
                v |= v >> 1;
                v |= v >> 2;
                v |= v >> 4;
                v |= v >> 8;
                v |= v >> 16;
                return _tab32[(uint)(v * 0x07C4ACDD) >> 27];
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static int NextMultipleOf(int value, int of)
            {
                return IntDivideCeil(value, of) * of;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static int IntDivideCeil(int dividend, int divisor)
            {
                return (dividend + divisor - 1) / divisor;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static int BitLength(uint num)
            {
                switch (num)
                {
                    case 0: return 0;
                    case 1: return 1;
                    case 2: return 2;
                }
                var i = 2;
                for (; i < 32; i++)
                {
                    var comp = 1 << i;
                    if (num < comp)
                        break;
                }
                return i;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static int Max<TCollection, TElem>(TCollection collection, Func<TElem, int> func)
                where TCollection : IEnumerable<TElem>
            {
                var max = int.MinValue;
                foreach (var elem in collection)
                {
                    var num = func(elem);
                    if (num > max)
                        max = num;
                }
                return max;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static int Sum<TCollection, TElem>(TCollection collection, Func<TElem, int> func)
                where TCollection : IEnumerable<TElem>
            {
                var sum = 0;
                foreach (var elem in collection)
                {
                    sum += func(elem);
                }
                return sum;
            }
        }
    }

    partial class SimUtil
    {
        public static class Path
        {
            private static readonly StringBuilder _pathBuilder = new();
            private static readonly List<ReadOnlyMemory<char>> _pathSegments = new();

            public static List<ReadOnlyMemory<char>> PathSegmentsHolder
            {
                get
                {
                    _pathSegments.Clear();
                    return _pathSegments;
                }
            }
            public static ReadOnlyMemory<char> BuildPath(List<ReadOnlyMemory<char>> basePaths, ReadOnlySpan<char> fileName)
            {
                _pathBuilder.Clear();
                foreach (var path in basePaths)
                {
                    _pathBuilder.Append(path.Span);
                    _pathBuilder.Append('/');
                }
                if (fileName.IsEmpty)
                {
                    if (_pathBuilder.Length > 0)
                        _pathBuilder.Remove(_pathBuilder.Length - 1, 1);
                }
                else
                    _pathBuilder.Append(fileName);
                return _pathBuilder.ToString().AsMemory();
            }
        }
    }
}
