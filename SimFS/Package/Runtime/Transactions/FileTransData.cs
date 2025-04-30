namespace SimFS
{
    internal class FileTransData
    {
        public int originLength;
        public SortedList<WriteOperation> writes;
        public BufferHolder<byte> attributes;

        public void Clear()
        {
            if (attributes.IsValid)
            {
                attributes.Dispose();
                attributes = default;
            }
            if (writes != null)
            {
                foreach (var op in writes)
                {
                    op.Data.Dispose();
                }
            }
        }

        public void Dispose(TransactionPooling tp)
        {
            Clear();
            if (writes != null)
            {
                tp.WriteOpsPool.Return(writes);
                writes = null;
            }
        }
    }
}
