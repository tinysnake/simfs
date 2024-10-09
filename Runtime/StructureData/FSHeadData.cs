using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SimFS
{
    [StructLayout(LayoutKind.Explicit)]
    internal readonly struct FSHeadData
    {
        public const byte VERSION = 0;
        public const int RESERVED_SIZE = 256;
        public const int MAX_ATTR_SIZE = 32;
        public const int MIN_POINTERS_COUNT = 3;
        public const int MAX_POINTERS_COUNT = 7;
        public const ushort MIN_BLOCK_SIZE = 128;
        public const ushort MAX_BLOCK_SIZE = 1024;
        public static readonly int MemSize = Marshal.SizeOf<FSHeadData>();
        public static readonly byte[] SIGNATURE = Encoding.UTF8.GetBytes("SMFS");

        public FSHeadData(ushort blockSize, byte attributeSize)
        {
            this.blockSize = blockSize;
            this.attributeSize = attributeSize;
            version = VERSION;
            blockGroupCount = 0;
            inodeBlockPointersCount = GetPointersSize(blockSize);
        }

        public FSHeadData(ushort blockSize, byte attributeSize, int blockGroupCount)
        {
            version = VERSION;
            this.blockSize = blockSize;
            inodeBlockPointersCount = GetPointersSize(blockSize);
            this.attributeSize = attributeSize;
            this.blockGroupCount = blockGroupCount;
        }

        public static byte GetPointersSize(uint blockSize)
        {
            if (blockSize < 1024)
                return MIN_POINTERS_COUNT;
            return MAX_POINTERS_COUNT;
        }

        [FieldOffset(0)]
        public readonly byte version;
        [FieldOffset(1)]
        public readonly ushort blockSize;
        [FieldOffset(3)]
        public readonly byte inodeBlockPointersCount;
        [FieldOffset(4)]
        public readonly byte attributeSize;
        [FieldOffset(5)]
        public readonly int blockGroupCount;

        public readonly void ThrowIfNotValid()
        {
            if (version != VERSION)
                throw new NotSupportedException($"version: {version} is not supported.");
            if (blockSize < MIN_BLOCK_SIZE || blockSize > MAX_BLOCK_SIZE)
                throw new IndexOutOfRangeException($"blockSize: {blockSize} has to be in range of [{MIN_BLOCK_SIZE}, {MAX_BLOCK_SIZE}].");
            if (SimUtil.Number.NextPowerOf2(blockSize) != blockSize)
                throw new NotSupportedException($"blockSize: {blockSize} has to be power of 2");
            if (attributeSize > MAX_ATTR_SIZE)
                throw new IndexOutOfRangeException($"attributeSize: {attributeSize} cannot be larger than {MAX_ATTR_SIZE}.");
            if (inodeBlockPointersCount < MIN_POINTERS_COUNT || inodeBlockPointersCount > MAX_POINTERS_COUNT)
                throw new IndexOutOfRangeException($"inodeBlockPointersCount: {inodeBlockPointersCount} has to be in range of [{MIN_POINTERS_COUNT}, {MAX_POINTERS_COUNT}].");
        }
    }
}
