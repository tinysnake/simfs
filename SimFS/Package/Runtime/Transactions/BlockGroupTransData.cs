using System;
using System.Buffers;

namespace SimFS
{
    internal struct BlockGroupTransData
    {
        private byte[] _inodeBitmap;
        private byte[] _blockBitmap;
        public Memory<byte> InodeBitmap { get; private set; }
        public Memory<byte> BlockBitmap { get; private set; }

        public void Initialize(int inodeBitmapLength, int blockBitmapLength)
        {
            if (_inodeBitmap == null)
            {
                _inodeBitmap = ArrayPool<byte>.Shared.Rent(inodeBitmapLength);
                InodeBitmap = _inodeBitmap.AsMemory()[..inodeBitmapLength];
            }
            if (_blockBitmap == null)
            {
                _blockBitmap = ArrayPool<byte>.Shared.Rent(blockBitmapLength);
                BlockBitmap = _blockBitmap.AsMemory()[..blockBitmapLength];
            }
        }

        public void Dispose()
        {
            if (_inodeBitmap != null)
            {
                ArrayPool<byte>.Shared.Return(_inodeBitmap);
                _inodeBitmap = null;
            }
            if (_blockBitmap != null)
            {
                ArrayPool<byte>.Shared.Return(_blockBitmap);
                _blockBitmap = null;
            }
        }
    }
}
