using System;

namespace SimFS
{
    internal static partial class SimFSExtensions
    {
        public static int Split(this ReadOnlySpan<char> chars, ReadOnlySpan<char> spliter, Span<Range> ranges, bool removeEmptySegments = false)
        {
            var from = 0;
            var rangeIndex = 0;
            while (from < chars.Length)
            {
                var to = chars[from..].IndexOf(spliter);
                if (to < 0)
                    to = chars.Length;
                else
                    to += from;
                if (!(removeEmptySegments && to - from <= 1))
                {
                    var range = new Range(from, to);
                    ranges[rangeIndex++] = range;
                }
                from = to + 1;
            }
            return rangeIndex;
        }
    }
}
