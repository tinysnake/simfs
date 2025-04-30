using System.Collections.Generic;

namespace SimFS
{
    internal class WriteOperationComparer : IComparer<WriteOperation>
    {
        public static WriteOperationComparer Default { get; } = new();
        public int Compare(WriteOperation x, WriteOperation y) => x.Location - y.Location;
    }

    internal readonly struct WriteOperation
    {
        public WriteOperation(int location, BufferHolder<byte> data)
        {
            Location = location;
            Data = data;
        }
        public int Location { get; }
        public BufferHolder<byte> Data { get; }

        public void Deconstruct(out int location, out BufferHolder<byte> data)
        {
            location = Location;
            data = Data;
        }
    }
}
