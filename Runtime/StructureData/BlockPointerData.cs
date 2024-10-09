using System.Runtime.InteropServices;

namespace SimFS
{
    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    internal readonly struct BlockPointerData
    {
        public static readonly int MemSize = Marshal.SizeOf<BlockPointerData>();

        public BlockPointerData(int location, byte count)
        {
            globalIndex = location;
            blockCount = count;
        }

        [FieldOffset(0)]
        public readonly int globalIndex;
        [FieldOffset(4)]
        public readonly byte blockCount;

        public bool IsEmpty => globalIndex == 0 && blockCount == 0;
    }
}
