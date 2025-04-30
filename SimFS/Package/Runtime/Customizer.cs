using System.Collections.Generic;
using System;

namespace SimFS
{
    public class Customizer
    {
        public IEqualityComparer<ReadOnlyMemory<char>> NameComparer { get; set; } = SimFS.NameComparer.Ordinal;
        public int BufferSize { get; set; } = 0;
        public int MaxCachedDirectoires { get; set; } = 100;
        public int MaxCachedBlockGroupHead { get; set; } = 12800;
        public int MaxCachedTransactions { get; set; } = 100;
        public int TransactionsMaxCapacity { get; set; } = 16;
        public int TransactionsMaxCollectionCapacity { get; set; } = 32;
    }
}
