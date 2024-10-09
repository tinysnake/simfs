namespace SimFS
{
    internal class FSHead
    {
        public FSHead()
        {

        }

        public FSHead(FSHeadData data)
        {
            Update(data);
        }

        public ushort BlockSize { get; private set; }
        public byte AttributeSize { get; private set; }
        public int InodeBlockPointersCount { get; private set; }
        public int BlockGroupCount { get; set; }

        public void Update(FSHeadData headData)
        {
            BlockSize = headData.blockSize;
            AttributeSize = headData.attributeSize;
            InodeBlockPointersCount = headData.inodeBlockPointersCount;
            BlockGroupCount = headData.blockGroupCount;
        }

        public FSHeadData ToHeadData() => new(BlockSize, AttributeSize, BlockGroupCount);

        public void ThrowIfNotValid()
        {
            if (BlockSize == 0 || InodeBlockPointersCount <= 0)
                throw new SimFSException(ExceptionType.InvalidHead);
        }
    }
}
