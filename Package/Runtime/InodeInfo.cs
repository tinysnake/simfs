namespace SimFS
{
    internal readonly struct InodeInfo
    {
        public static InodeInfo Empty => new(-1, default);

        public InodeInfo(int globalIndex, InodeData data)
        {
            this.globalIndex = globalIndex;
            this.data = data;
        }

        public readonly int globalIndex;
        public readonly InodeData data;

        public void Deconstruct(out int globalIndex, out InodeData data)
        {
            globalIndex = this.globalIndex;
            data = this.data;
        }

        public bool IsEmpty => globalIndex < 0 || data.IsEmpty;

        public void ThrowsIfNotValid()
        {
            if (IsEmpty)
                throw new SimFSException(ExceptionType.InvalidInode, "inode index:" + globalIndex);
        }
    }
}
