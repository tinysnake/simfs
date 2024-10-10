using System.Collections.Generic;
using System;

namespace SimFS
{
    public class Customizer
    {
        public IEqualityComparer<ReadOnlyMemory<char>> NameComparer { get; set; } = SimFS.NameComparer.Ordinal;
        public int BufferSize { get; set; } = 8192;
        public int MaxCachedDirectoires { get; set; } = 1000;
        public int MaxCachedBlockGroupHead { get; set; } = 12800;
    }
}
