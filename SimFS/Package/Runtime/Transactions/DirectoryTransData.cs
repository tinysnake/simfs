using System;
using System.Collections.Generic;

namespace SimFS
{
    internal readonly struct DirectoryEntryChangeData
    {
        public DirectoryEntryChangeData(DirectoryEntryData entryData, ReadOnlyMemory<char> name)
        {
            EntryData = entryData;
            Name = name;
        }

        public DirectoryEntryData EntryData { get; }
        public ReadOnlyMemory<char> Name { get; }

        public void Deconstruct(out DirectoryEntryData entryData, out ReadOnlyMemory<char> name)
        {
            entryData = EntryData;
            name = Name;
        }
    }

    internal class DirectoryTransactionData
    {
        public HashSet<int> childrenChanges;
        public Dictionary<int, DirectoryEntryChangeData> entryChanges;
        public int originEntryCount;

        public void Clear()
        {
            childrenChanges?.Clear();
            entryChanges?.Clear();
            originEntryCount = 0;
        }

        public void Dispose(TransactionPooling tp)
        {
            Clear();
            if (childrenChanges != null)
            {
                tp.DirChildrenChangesPool.Return(childrenChanges);
                childrenChanges = null;
            }
            if (entryChanges != null)
            {
                tp.DirEntryChangesPool.Return(entryChanges);
                entryChanges = null;
            }
            originEntryCount = 0;
            childrenChanges = null;
            entryChanges = null;
        }
    }
}
