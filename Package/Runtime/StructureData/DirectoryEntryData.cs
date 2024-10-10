using System.Runtime.CompilerServices;

namespace SimFS
{
    internal readonly struct DirectoryEntryData
    {
        public const byte MIN_NAME_LENGTH = 4;
        public const byte MAX_NAME_LENGTH = 248;
        private const byte DATA_SIZE_OF_THE_REST = 7;


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetMaxNameLength(byte entryLength)
        {
            if (entryLength <= DATA_SIZE_OF_THE_REST)
                throw new System.ArgumentOutOfRangeException(nameof(entryLength) + "is too small");
            return (byte)(entryLength - DATA_SIZE_OF_THE_REST);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetRegulatedNameLength(int nameLength)
        {
            if ((uint)nameLength > (uint)byte.MaxValue)
                throw new System.ArgumentOutOfRangeException($"{nameof(nameLength)} should shorter than {MAX_NAME_LENGTH}");
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

        public DirectoryEntryData(int entryLength, int nameLength, int inodeGlobalIndex, InodeUsage usage)
        {
            if (entryLength > byte.MaxValue)
                throw new System.ArgumentOutOfRangeException($"the entryLength is longer than {byte.MaxValue}");
            if (nameLength > MAX_NAME_LENGTH)
                throw new System.ArgumentOutOfRangeException($"the nameLength is longer than {MAX_NAME_LENGTH}");
            if (nameLength + DATA_SIZE_OF_THE_REST > entryLength)
                throw new System.ArgumentException($"nameLength should always smaller than entryLength - {DATA_SIZE_OF_THE_REST}");
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
                throw new System.ArgumentOutOfRangeException(nameof(nameLength));
            return new DirectoryEntryData(entryLength, nameLength, inodeGlobalIndex, usage);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DirectoryEntryData Free() => new(entryLength, nameLength, -1, InodeUsage.Unused);
    }
}
