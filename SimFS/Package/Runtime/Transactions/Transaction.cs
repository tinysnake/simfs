using System;
using System.Collections.Generic;

namespace SimFS
{
    public enum TransactionMode
    {
        /// <summary>
        /// The default mode, you can call <see cref="Transaction.Commit"/> multple times, and will auto dispose when a <see cref="EnsureTransaction"/> Disposes.
        /// </summary>
        Immediate,

        /// <summary>
        /// This mode usally used on batch operations, the transaction will not auto dispose unless you explicitly call <see cref="Transaction.Dispose"/>.
        /// </summary>
        Manual,

        /// <summary>
        /// useful when open a file, the transaction will auto dispose when the correlated <see cref="SimFileStream"/> is closed.
        /// </summary>
        Temproary,
    }

    public readonly struct EnsureTransaction : IDisposable
    {
        public EnsureTransaction(Transaction trans, Func<Transaction> transactionFactory)
        {
            if (trans == null)
            {
                if (transactionFactory == null)
                    throw new ArgumentNullException(nameof(transactionFactory));
                Value = transactionFactory.Invoke();
            }
            else
                Value = trans;
        }

        internal EnsureTransaction(Transaction trans, FSMan fsMan, TransactionMode mode)
        {
            if (fsMan == null)
                throw new ArgumentNullException(nameof(fsMan));
            if (trans == null)
                Value = fsMan.BeginTransaction(mode, null);
            else
                Value = trans;
        }

        public Transaction Value { get; }

        public void Dispose()
        {
            if (Value == null || Value.Disposed)
                return;
            if (Value.Mode == TransactionMode.Immediate)
                Value.Dispose();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="commit">true is commit, false is rollback</param>
        public void Complete(bool commit)
        {
            if (Value == null || Value.Disposed)
                return;
            if (Value.Mode == TransactionMode.Immediate)
                Value.Complete(commit);
        }

        public static implicit operator Transaction(EnsureTransaction et) => et.Value;
    }

    public class Transaction : IDisposable
    {
        private static ushort _globalId;

        private static ushort GetGlobalId()
        {
            var result = ++_globalId;
            if (result == 0)
                result = ++_globalId;
            return result;
        }

        internal Transaction()
        {
        }

        internal void ReInitialize(FSMan fsMan, TransactionMode mode, string name)
        {
            _fsMan = fsMan;
            _friendlyName = name;
            _id = GetGlobalId();
            Mode = mode;

            _isCommiting = false;
        }

        private FSMan _fsMan;
        private int _id;
        private string _friendlyName;

        private Dictionary<int, Dictionary<int, InodeData>> _inodeChanges;
        private Dictionary<int, BlockGroupTransData> _blockGroupChanges;
        private Dictionary<int, FileTransData> _fileChanges;
        private bool _isCommiting;
        private bool _isSavingFiles;

        public int ID => _id;
        public string FriendlyName => string.IsNullOrEmpty(_friendlyName) ? _id.ToString() : _friendlyName;
        public TransactionMode Mode { get; private set; }
        public bool Disposed => _fsMan == null;

        internal void DirectoryBeforeChange(SimDirectory dir, int count)
        {
            ThrowsIfNotValid();
            if (dir == null)
                throw new ArgumentNullException(nameof(dir));
            var tp = _fsMan.Pooling.TransactionPooling;

            if (dir.Parent != null)
            {
                var parentDir = dir.Parent;
                var lastDir = dir;
                while (parentDir != null)
                {
                    parentDir.transactionData ??= tp.DirTransDataDictPool.Get();
                    if (!parentDir.transactionData.TryGetValue(ID, out var parentTransData))
                    {
                        parentTransData = tp.DirTransDataPool.Get();
                        parentTransData.originEntryCount = -1;
                        parentDir.transactionData[ID] = parentTransData;
                    }

                    parentTransData.childrenChanges ??= tp.DirChildrenChangesPool.Get();
                    if (parentTransData.childrenChanges.Contains(lastDir.Index))
                        break;

                    parentTransData.childrenChanges.Add(lastDir.Index);
                    lastDir = parentDir;
                    parentDir = parentDir.Parent;
                }
            }


            dir.transactionData ??= tp.DirTransDataDictPool.Get();
            if (dir.transactionData.TryGetValue(ID, out var selfTransData) && selfTransData.originEntryCount >= 0)
                return;

            selfTransData ??= tp.DirTransDataPool.Get();
            selfTransData.originEntryCount = count;
            dir.transactionData[ID] = selfTransData;
        }

        internal void DirectoryBeforeChange(SimDirectory dir, int entryIndex, DirectoryEntryData entryData,
            ReadOnlyMemory<char> name)
        {
            ThrowsIfNotValid();
            if (dir == null)
                throw new ArgumentNullException(nameof(dir));
            if (!dir.transactionData.TryGetValue(ID, out var dirTransData))
                throw new SimFSException(ExceptionType.InvalidOperation,
                    "you must record entryCount before record any entryData");
            dirTransData.entryChanges ??= _fsMan.Pooling.TransactionPooling.DirEntryChangesPool.Get();
            var entryChanges = dirTransData.entryChanges;
            if (entryChanges.ContainsKey(entryIndex))
                return;

            entryChanges[entryIndex] = new DirectoryEntryChangeData(entryData, name);
        }

        internal void BlockGroupBeforeChange(int blockGroupId, ReadOnlySpan<byte> inodeBitmap,
            ReadOnlySpan<byte> blockBitmap)
        {
            ThrowsIfNotValid();
            _blockGroupChanges ??= _fsMan.Pooling.TransactionPooling.BlockGroupMetaPool.Get();
            if (_blockGroupChanges.ContainsKey(blockGroupId))
                return;

            var data = new BlockGroupTransData();
            data.Initialize(inodeBitmap.Length, blockBitmap.Length);

            inodeBitmap.CopyTo(data.InodeBitmap.Span);
            blockBitmap.CopyTo(data.BlockBitmap.Span);

            _blockGroupChanges[blockGroupId] = data;
        }

        internal void InodeBeforeChange(int bgIndex, int inodeIndex, InodeData data)
        {
            ThrowsIfNotValid();
            _inodeChanges ??= _fsMan.Pooling.TransactionPooling.InodeDataForBlockGroupPool.Get();
            if (!_inodeChanges.TryGetValue(bgIndex, out var dict))
            {
                dict = _fsMan.Pooling.TransactionPooling.InodeDataPool.Get();
                _inodeChanges[bgIndex] = dict;
            }

            if (dict.ContainsKey(inodeIndex))
                return;

            dict[inodeIndex] = data;
        }

        internal void InodeBeforeChange(int globalIndex, InodeData data)
        {
            ThrowsIfNotValid();
            var (bgIndex, inodeIndex) = FSMan.GetLocalIndex(globalIndex, _fsMan.Head.BlockSize);
            InodeBeforeChange(bgIndex, inodeIndex, data);
        }

        internal void FileBeforeChange(int inodeGlobalIndex, int length)
        {
            ThrowsIfNotValid();
            if (_isSavingFiles)
                throw new SimFSException(ExceptionType.TransactionIsAlreadyCommiting, "cannot write new data while transaction is commiting");
            _fileChanges ??= _fsMan.Pooling.TransactionPooling.FileTransDataDictPool.Get();
            if (_fileChanges.ContainsKey(inodeGlobalIndex))
                return;

            var tp = _fsMan.Pooling.TransactionPooling;
            var ftd = tp.FileTransDataPool.Get();
            _fileChanges[inodeGlobalIndex] = ftd;
            ftd.originLength = length;
        }

        internal void FileAttributesBeforeChange(int inodeGlobalIndex, ReadOnlySpan<byte> attributes)
        {
            ThrowsIfNotValid();
            if (!(_fileChanges?.TryGetValue(inodeGlobalIndex, out var ftd) ?? false))
                throw new SimFSException(ExceptionType.InvalidOperation, "you must record fileLength before write any data");
            if (ftd.attributes.IsValid)
                return;
            ftd.attributes = _fsMan.Pooling.RentBuffer(out var span, attributes.Length, true);
            attributes.CopyTo(span);
        }

        internal void FileWrite(int inodeGlobalIndex, int position, BufferHolder<byte> buffer)
        {
            ThrowsIfNotValid();
            if (_isSavingFiles)
                throw new SimFSException(ExceptionType.TransactionIsAlreadyCommiting, "cannot write new data while transaction is commiting");
            if (!(_fileChanges?.TryGetValue(inodeGlobalIndex, out var ftd) ?? false))
                throw new SimFSException(ExceptionType.InvalidOperation, "you must record fileLength before write any data");

            ftd.writes ??= _fsMan.Pooling.TransactionPooling.WriteOpsPool.Get();
            ftd.writes.Add(new WriteOperation(position, buffer));
        }

        private void ThrowsIfNotValid()
        {
            if (_fsMan == null)
                throw new SimFSException(ExceptionType.TransactionAlreadyDisposed);
        }

        public void Commit()
        {
            ThrowsIfNotValid();
            if (_isCommiting)
                throw new SimFSException(ExceptionType.TransactionIsAlreadyCommiting, nameof(Commit));
            _isCommiting = true;
            var tp = _fsMan.Pooling.TransactionPooling;

            var rangeList = tp.RangeListPool.Get();
            var stack = tp.DirSaveStackPool.Get();
            try
            {
                stack.Add((_fsMan.RootDirectory, false));
                while (stack.Count > 0)
                {
                    var (dir, flag) = stack[^1];
                    if (!flag)
                    {
                        stack[^1] = (dir, true);
                        dir.SaveChangesFillChildren(ID, stack);
                    }
                    else
                    {
                        stack.RemoveAt(stack.Count - 1);
                        rangeList.Clear();
                        dir.SaveChanges(this, rangeList);
                    }
                }
            }
            finally
            {
                tp.RangeListPool.Return(rangeList);
                tp.DirSaveStackPool.Return(stack);
            }

            if (_fileChanges != null)
            {
                _isSavingFiles = true;
                try
                {
                    var bs = _fsMan.Head.BlockSize;
                    foreach (var (inodeGlobalIndex, changes) in _fileChanges)
                    {
                        var openedFile = _fsMan.GetLoadedFileStream(inodeGlobalIndex);
                        if (openedFile == null)
                        {
                            var (gi, inodeIndex) = FSMan.GetLocalIndex(inodeGlobalIndex, bs);
                            var bg = _fsMan.GetBlockGroup(gi);
                            if (!bg.InodeBitmap.Check(inodeIndex))
                                continue;
                            var inode = bg.GetInode(inodeIndex);
                            using var fs = _fsMan.DangerouslyLoadFileStream(inode, FileAccess.ReadWrite, this, null, bg);
                            fs.SaveChanges(this, changes);
                        }
                        else
                        {
                            openedFile.SaveChanges(this, changes);
                        }
                    }
                }
                finally
                {
                    _isSavingFiles = false;
                }
            }

            if (_blockGroupChanges != null)
            {
                foreach (var (bgId, _) in _blockGroupChanges)
                {
                    var bg = _fsMan.GetBlockGroup(bgId);
                    bg.SaveMetaChanges();
                }
            }

            if (_blockGroupChanges != null)
            {
                foreach (var (bgId, dict) in _inodeChanges)
                {
                    var bg = _fsMan.GetBlockGroup(bgId);
                    bg.SaveInodeChanges(dict);
                }
            }

            if (Mode == TransactionMode.Immediate)
                Clear();
            else
                TruelyDispose();
        }

        public void Rollback()
        {
            if (_fsMan == null)
                throw new SimFSException(ExceptionType.TransactionAlreadyDisposed);
            if (_isCommiting)
                throw new SimFSException(ExceptionType.TransactionIsAlreadyCommiting, nameof(Rollback));

            _isCommiting = true;
            var tp = _fsMan.Pooling.TransactionPooling;

            foreach (var (bgId, dict) in _inodeChanges)
            {
                var bg = _fsMan.GetBlockGroup(bgId);
                bg.RevertInodeChanges(dict);
            }

            foreach (var (bgId, meta) in _blockGroupChanges)
            {
                var bg = _fsMan.GetBlockGroup(bgId);
                bg.RevertMetaChanges(meta.BlockBitmap.Span, meta.InodeBitmap.Span);
            }

            var stack = tp.DirSaveStackPool.Get();
            try
            {
                stack.Add((_fsMan.RootDirectory, false));
                while (stack.Count > 0)
                {
                    var (dir, flag) = stack[^1];
                    if (!flag)
                    {
                        stack[^1] = (dir, true);
                        dir.SaveChangesFillChildren(ID, stack);
                    }
                    else
                    {
                        stack.RemoveAt(stack.Count - 1);
                        dir.RevertChanges(ID);
                    }
                }
            }
            finally
            {
                tp.DirSaveStackPool.Return(stack);
            }

            var bs = _fsMan.Head.BlockSize;
            foreach (var (inodeGlobalIndex, changes) in _fileChanges)
            {
                var openedFile = _fsMan.GetLoadedFileStream(inodeGlobalIndex);
                openedFile?.RevertChanges(changes);
            }

            TruelyDispose();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="commit">true is commit, false is rollback</param>
        public void Complete(bool commit)
        {
            if (Disposed)
                return;
            if (commit)
                Commit();
            else
                Rollback();
        }

        public void Dispose()
        {
            Commit();
            if (!Disposed)
                TruelyDispose();
        }

        private void Clear()
        {
            var tp = _fsMan.Pooling.TransactionPooling;

            if (_inodeChanges != null)
            {
                foreach (var (_, inodeChange) in _inodeChanges)
                {
                    tp.InodeDataPool.Return(inodeChange);
                }

                tp.InodeDataForBlockGroupPool.Return(_inodeChanges);
                _inodeChanges = null;
            }

            if (_blockGroupChanges != null)
            {
                foreach (var (_, meta) in _blockGroupChanges)
                {
                    meta.Dispose();
                }

                tp.BlockGroupMetaPool.Return(_blockGroupChanges);
                _blockGroupChanges = null;
            }

            if (_fileChanges != null)
            {
                foreach (var (_, ftd) in _fileChanges)
                {
                    ftd.Dispose(tp);
                }

                tp.FileTransDataDictPool.Return(_fileChanges);
                _fileChanges = null;
            }

            _isCommiting = false;
        }

        private void TruelyDispose()
        {
            if (_fsMan == null)
                return;
            try
            {
                _friendlyName = null;
                Clear();
                _fsMan.EndTransaction(this);
            }
            finally
            {
                _fsMan = null;
            }
        }
    }
}