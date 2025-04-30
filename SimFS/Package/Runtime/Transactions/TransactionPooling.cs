using System.Collections.Generic;

namespace SimFS
{
    internal class TransactionPooling
    {
        public TransactionPooling(int maxCapacity, int maxCollectionCapacity)
        {
            MaxCapacity = maxCapacity;
            MaxCollectionCapacity = maxCollectionCapacity;
            InodeDataForBlockGroupPool = new CrudeObjectPool<Dictionary<int, Dictionary<int, InodeData>>>(
                () => new Dictionary<int, Dictionary<int, InodeData>>(),
                onReturn: x => TrimExcess(x, maxCapacity), //because this is a nested dictionary, so the smaller value of maxCapacity
                maxCapacity: maxCapacity);
            InodeDataPool = new CrudeObjectPool<Dictionary<int, InodeData>>(
                () => new Dictionary<int, InodeData>(),
                onReturn: x => TrimExcess(x, MaxCollectionCapacity),
                maxCapacity: maxCapacity);
            BlockGroupMetaPool = new CrudeObjectPool<Dictionary<int, BlockGroupTransData>>(
                () => new Dictionary<int, BlockGroupTransData>(),
                onReturn: x => TrimExcess(x, MaxCollectionCapacity),
                maxCapacity: maxCapacity);
            DirTransDataDictPool = new CrudeObjectPool<Dictionary<int, DirectoryTransactionData>>(
                () => new Dictionary<int, DirectoryTransactionData>(),
                onReturn: x => TrimExcess(x, MaxCollectionCapacity),
                maxCapacity: MaxCollectionCapacity);
            DirTransDataPool = new CrudeObjectPool<DirectoryTransactionData>(
                () => new DirectoryTransactionData(),
                maxCapacity: MaxCollectionCapacity);
            DirChildrenChangesPool = new CrudeObjectPool<HashSet<int>>(
                () => new HashSet<int>(),
                onReturn: x => x.Clear(),
                maxCapacity: MaxCollectionCapacity);
            DirEntryChangesPool = new CrudeObjectPool<Dictionary<int, DirectoryEntryChangeData>>(
                () => new Dictionary<int, DirectoryEntryChangeData>(),
                onReturn: x => TrimExcess(x, MaxCollectionCapacity),
                maxCapacity: maxCapacity);
            RangeListPool = new CrudeObjectPool<RangeList>(
                () => new RangeList(),
                onReturn: x => x.TrimExcess(MaxCollectionCapacity),
                maxCapacity: maxCapacity);
            DirSaveStackPool = new CrudeObjectPool<List<(SimDirectory, bool)>>(
                () => new List<(SimDirectory, bool)>(),
                onReturn: x => x.TrimExcess(),
                maxCapacity: maxCollectionCapacity);
            FileTransDataDictPool = new CrudeObjectPool<Dictionary<int, FileTransData>>(
                () => new Dictionary<int, FileTransData>(),
                onReturn: x => TrimExcess(x, MaxCollectionCapacity),
                maxCapacity: maxCollectionCapacity);
            FileTransDataPool = new CrudeObjectPool<FileTransData>(
                () => new FileTransData(),
                maxCapacity: maxCollectionCapacity);
            WriteOpsPool = new CrudeObjectPool<SortedList<WriteOperation>>(
                () => new SortedList<WriteOperation>(WriteOperationComparer.Default) 
                { 
                    InsertPolicyOnSameOrder = SortedListPolicy.OnLast 
                },
                onReturn: x => TrimExcess(x, MaxCollectionCapacity),
                maxCapacity: maxCollectionCapacity);
            CompactWriteOpsPool = new CrudeObjectPool<List<WriteOperation>>(
                () => new List<WriteOperation>(),
                onReturn: x => TrimExcess(x, MaxCollectionCapacity),
                maxCapacity: maxCollectionCapacity);
        }

        internal int MaxCapacity { get; }
        internal int MaxCollectionCapacity { get; }
        internal IObjectPool<Dictionary<int, Dictionary<int, InodeData>>> InodeDataForBlockGroupPool { get; }
        internal IObjectPool<Dictionary<int, InodeData>> InodeDataPool { get; }
        internal IObjectPool<Dictionary<int, BlockGroupTransData>> BlockGroupMetaPool { get; }
        internal IObjectPool<Dictionary<int, DirectoryTransactionData>> DirTransDataDictPool { get; }
        internal IObjectPool<DirectoryTransactionData> DirTransDataPool { get; }
        internal IObjectPool<HashSet<int>> DirChildrenChangesPool { get; }
        internal IObjectPool<Dictionary<int, DirectoryEntryChangeData>> DirEntryChangesPool { get; }
        internal IObjectPool<RangeList> RangeListPool { get; }
        internal IObjectPool<List<(SimDirectory, bool)>> DirSaveStackPool { get; }
        internal IObjectPool<Dictionary<int, FileTransData>> FileTransDataDictPool { get; }
        internal IObjectPool<FileTransData> FileTransDataPool { get; }
        internal IObjectPool<SortedList<WriteOperation>> WriteOpsPool { get; }
        internal IObjectPool<List<WriteOperation>> CompactWriteOpsPool { get; }

        private static void TrimExcess<T>(List<T> list, int maxCapacity)
        {
            list.Clear();
            if (list.Capacity > maxCapacity)
                list.Capacity = maxCapacity;
        }
        private static void TrimExcess<T>(SortedList<T> list, int maxCapacity)
        {
            list.Clear();
            if (list.Capacity > maxCapacity)
                list.Capacity = maxCapacity;
        }
        private static void TrimExcess<TKey, TValue>(Dictionary<TKey, TValue> dict, int maxCapacity)
        {
            dict.Clear();
            dict.TrimExcess(maxCapacity);
        }
    }
}
