using System;
using System.Collections.Generic;

namespace SimFS
{
    public static class NameComparer
    {
        public static IEqualityComparer<ReadOnlyMemory<char>> Ordinal { get; } = new OrdinalComparer();
        public static IEqualityComparer<ReadOnlyMemory<char>> IgnoreCase { get; } = new IgnoreCaseComparer();

        internal class OrdinalComparer : IEqualityComparer<ReadOnlyMemory<char>>
        {
            public bool Equals(ReadOnlyMemory<char> x, ReadOnlyMemory<char> y)
            {
                return x.Span.CompareTo(y.Span, StringComparison.Ordinal) == 0;
            }

            public int GetHashCode(ReadOnlyMemory<char> obj)
            {
                var hashcode = new HashCode();
                foreach (var ch in obj.Span)
                {
                    hashcode.Add(ch);
                }

                return hashcode.ToHashCode();
            }
        }

        internal class IgnoreCaseComparer : IEqualityComparer<ReadOnlyMemory<char>>
        {
            public bool Equals(ReadOnlyMemory<char> x, ReadOnlyMemory<char> y)
            {
                return x.Span.CompareTo(y.Span, StringComparison.OrdinalIgnoreCase) == 0;
            }

            public int GetHashCode(ReadOnlyMemory<char> obj)
            {
                var hashcode = new HashCode();
                foreach (var ch in obj.Span)
                {
                    hashcode.Add(char.ToUpper(ch));
                }

                return hashcode.ToHashCode();
            }
        }
    }
}
