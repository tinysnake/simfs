using System;

namespace SimFS
{
    internal enum ReadWriteState
    {
        None,
        Reading,
        Writing,
    }

    internal class ReadWriteLock
    {
        public ReadWriteState State { get; private set; }
        internal void ChangeState(ReadWriteState state)
        {
            if (state != ReadWriteState.None && State != ReadWriteState.None &&
                State != state)
                throw new SimFSException(ExceptionType.ReadWriteStateConflict);
            State = state;
        }
    }

    internal readonly struct ReadWriteLocker : IDisposable
    {
        public static ReadWriteLocker BeginRead(ReadWriteLock rwLock) => new(rwLock, ReadWriteState.Reading);
        public static ReadWriteLocker BeginWrite(ReadWriteLock rwLock) => new(rwLock, ReadWriteState.Writing);
        public ReadWriteLocker(ReadWriteLock rwLock, ReadWriteState state)
        {
            _lock = rwLock;
            _lock.ChangeState(state);
        }

        private readonly ReadWriteLock _lock;

        public void Dispose()
        {
            _lock.ChangeState(ReadWriteState.None);
        }
    }
}
