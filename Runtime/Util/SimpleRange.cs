using System;

namespace SimFS
{
    public struct SimpleRange
    {
        public SimpleRange(int start, int length)
        {
            this.start = start;
            this.length = length;
        }

        public int start;
        public int length;

        public readonly void Deconstruct(out int start, out int length)
        {
            start = this.start;
            length = this.length;
        }

        public static implicit operator Range(SimpleRange range)
        {
            return new Range(range.start, range.start + range.length);
        }
    }
}
