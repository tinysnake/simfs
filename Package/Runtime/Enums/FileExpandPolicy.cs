namespace SimFS
{
    public readonly struct FileExpandPolicy
    {
        public static FileExpandPolicy Default => new();
        public static FileExpandPolicy ByBlockCount(int count) => new(true, count);

        private FileExpandPolicy(bool expandByBlockCount, int blockCount)
        {
            this.expandByBlockCount = expandByBlockCount;
            this.blockCount = blockCount;
        }

        public readonly bool expandByBlockCount;
        public readonly int blockCount;
    }
}
