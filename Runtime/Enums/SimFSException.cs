using System;

namespace SimFS
{
    public enum ExceptionType
    {
        UnknownFileFormat,
        SpaceIsNotEmpty,
        FileIsNotEmpty,
        NotEnoughBits,
        InconsistantDataValue,
        BlockGroupNotTheSame,
        WrongBit,
        MultipleAllocation,
        MultipleFreeing,
        FileTooLarge,
        InvalidBlockGroup,
        InvalidInode,
        InvalidHead,
        InvalidDirectory,
        InvalidFileStream,
        AttributeBytesTooLong,
        NameTooLong,
        InvalidNameOrPath,
        NotAllocated,
        DirectoryNotFound,
        DirectoryAlreadyExists,
        FileNotFound,
        FileAlreadyExists,
        FileAlreadyOpended,
        BitmapMessedUp,
        ReadWriteStateConflict,
        InternalError,
        InvalidOperation,
    }

    public class SimFSException : Exception
    {
        public SimFSException(ExceptionType et) : base() { ExceptionType = et; }
        public SimFSException(ExceptionType et, string msg) : base(msg) { ExceptionType = et; }
        public SimFSException(ExceptionType et, string msg, Exception inner) : base(msg, inner) { ExceptionType = et; }

        public ExceptionType ExceptionType { get; }

        public override string Message => $"[{ExceptionType}]" + base.Message;
    }
}
