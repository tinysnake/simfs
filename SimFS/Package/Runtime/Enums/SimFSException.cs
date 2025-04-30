using System;

namespace SimFS
{
    public enum ExceptionType : int
    {
        //FileMan
        UnknownFileFormat = 100,
        FileIsNotEmpty = 101,
        UnableToAllocateBlockGroup = 102,
        InvalidHead = 103,
        //Generial
        InternalError = 200,
        InvalidOperation = 201,
        InconsistantDataValue = 202,
        UnsaveChangesMade = 203,
        //BlockGroup
        NotEnoughBits = 300,
        BlockGroupNotTheSame = 301,
        InvalidBlockGroup = 302,
        WrongBit = 303,
        BitmapMessedUp = 304,
        BlockIndexOutOfRange = 305,
        InvalidBlockOffset = 306,
        //FileStream
        InvalidFileStream = 400,
        FileTooLarge = 401,
        NotAllocated = 402,
        FileNotFound = 403,
        FileAlreadyExists = 404,
        FileWriteAccessAlreadyTaken = 405,
        NoWriteAccessRight = 406,
        NoReadWhenContentChanges = 407,
        MissingTransaction = 408,
        TransactionAlreadySet = 409,
        UnableToAllocateMoreSpaces = 410,
        UnableToDeleteFile = 410,
        //Inode
        InvalidInode = 500,
        UnableToAllocateInode = 501,
        //Directory
        InvalidDirectory = 600,
        InvalidNameOrPath = 601,
        DirectoryNotFound = 602,
        DirectoryAlreadyExists = 603,
        DirectoryAlreadyDisposed = 604,
        DirectoryAlreadyDeleted = 605,
        //Transaction
        TransactionAlreadyDisposed = 700,
        TransactionIsAlreadyCommiting = 701,
        TransactionMismatch = 702,
        //Buffers
        InvalidBuffer = 800,
        BufferIsOccupied = 801,
        BufferCannotBeOccupied = 802,
        UnableToRentBuffer = 803,

        //MultipleAllocation,
        //MultipleFreeing,
        //AttributeBytesTooLong,
        //NameTooLong,
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
