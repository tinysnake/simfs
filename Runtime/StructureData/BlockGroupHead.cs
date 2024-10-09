using System.Runtime.InteropServices;
using System.Text;

namespace SimFS
{
    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    internal readonly struct BlockGroupHead
    {
        public static readonly byte[] SIGNATURE = Encoding.UTF8.GetBytes("BKGP");
        public const int RESERVED_SIZE = 32;
        public static readonly int MemSize = Marshal.SizeOf<BlockGroupHead>();

        public BlockGroupHead(ushort freeBlocks, ushort freeInodes)
        {
            this.freeBlocks = freeBlocks;
            this.freeInodes = freeInodes;
        }

        [FieldOffset(0)]
        public readonly ushort freeBlocks;
        [FieldOffset(4)]
        public readonly ushort freeInodes;
    }
}
