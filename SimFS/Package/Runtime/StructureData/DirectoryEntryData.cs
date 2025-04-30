using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace SimFS
{
    internal readonly struct DirectoryEntryData
    {
        public const byte MIN_NAME_LENGTH = 4;
        public const byte MAX_NAME_LENGTH = 248;
        public const byte SIZE_MAX = MAX_NAME_LENGTH + DATA_SIZE_OF_THE_REST;
        private const byte DATA_SIZE_OF_THE_REST = 7;


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetMaxNameLength(byte entryLength)
        {
            if (entryLength <= DATA_SIZE_OF_THE_REST)
                throw new ArgumentOutOfRangeException(nameof(entryLength) + "is too small");
            return (byte)(entryLength - DATA_SIZE_OF_THE_REST);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetRegulatedNameLength(int nameLength)
        {
            if ((uint)nameLength > (uint)byte.MaxValue)
                throw new ArgumentOutOfRangeException($"{nameof(nameLength)} should shorter than {MAX_NAME_LENGTH}");
            return nameLength switch
            {
                < 64 => (byte)SimUtil.Number.NextPowerOf2((uint)nameLength),
                > 224 => 248,
                _ => (byte)SimUtil.Number.NextMultipleOf(nameLength, 32),
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetEntryLength(int nameLength)
        {
            return (byte)(GetRegulatedNameLength(nameLength) + DATA_SIZE_OF_THE_REST);
        }

        public static void WriteToBuffer(Span<byte> buffer, DirectoryEntryData entryData, ReadOnlyMemory<char> name)
        {
            var length = entryData.entryLength;
            if (buffer.Length < length)
                throw new ArgumentException($"buffer.Length: {buffer.Length} is shorter than the data: {length}");
            buffer = buffer[..length];
            buffer[0] = length;
            buffer[1] = (byte)Encoding.UTF8.GetBytes(name.Span, buffer[7..]);
            buffer[2] = (byte)entryData.usage;
            BitConverter.TryWriteBytes(buffer[3..], entryData.inodeGlobalIndex);
        }

        public static int ReadFromBuffer(Span<byte> buffer, out DirectoryEntryData entry, out string name)
        {
            entry = default;
            name = null;
            var entryLength = buffer[0];
            if (buffer.Length < entryLength)
                return 0;
            var nameLength = buffer[1];
            var usage = (InodeUsage)buffer[2];
            var inodeLocation = BitConverter.ToInt32(buffer[3..]);
            var nameBytes = buffer.Slice(7, nameLength);

            name = usage > InodeUsage.Unused ? Encoding.UTF8.GetString(nameBytes[..nameLength]) : null;
            entry = new DirectoryEntryData(entryLength, nameLength, inodeLocation, usage);

            return entryLength;
        }

        public DirectoryEntryData(int entryLength, int nameLength, int inodeGlobalIndex, InodeUsage usage)
        {
            if (entryLength > byte.MaxValue)
                throw new ArgumentOutOfRangeException($"the entryLength is longer than {byte.MaxValue}");
            if (nameLength > MAX_NAME_LENGTH)
                throw new ArgumentOutOfRangeException($"the nameLength is longer than {MAX_NAME_LENGTH}");
            if (nameLength + DATA_SIZE_OF_THE_REST > entryLength)
                throw new ArgumentException($"nameLength should always smaller than entryLength - {DATA_SIZE_OF_THE_REST}");
            this.entryLength = (byte)entryLength;
            this.nameLength = (byte)nameLength;
            this.inodeGlobalIndex = inodeGlobalIndex;
            this.usage = usage;
        }

        public readonly byte entryLength;
        public readonly byte nameLength;
        public readonly InodeUsage usage;
        public readonly int inodeGlobalIndex;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ThrowsIfNotValid()
        {
            if (entryLength <= 0 || nameLength <= 0 || usage == InodeUsage.Unused || inodeGlobalIndex < 0)
                throw new SimFSException(ExceptionType.InvalidDirectory);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ThrowsIfNotEmpty()
        {
            if (usage > InodeUsage.Unused || inodeGlobalIndex >= 0)
                throw new SimFSException(ExceptionType.InvalidDirectory);
        }

        public DirectoryEntryData ReUse(int nameLength, int inodeGlobalIndex, InodeUsage usage)
        {
            if (nameLength + DATA_SIZE_OF_THE_REST > entryLength)
                throw new ArgumentOutOfRangeException(nameof(nameLength));
            return new DirectoryEntryData(entryLength, nameLength, inodeGlobalIndex, usage);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DirectoryEntryData Free() => new(entryLength, nameLength, -1, InodeUsage.Unused);
    }
}
