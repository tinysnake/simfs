namespace SimFS
{
    internal class FileSharingData
    {
        public int GlobalInodeIndex { get; private set; }

        public SimFileStream WriteAccessInstance { get; private set; }

        public int ReadAccessCount { get; set; }

        public bool IsEmpty => ReadAccessCount <= 0 && WriteAccessInstance == null;

        public void Initialize(int globalInodeIndex)
        {
            GlobalInodeIndex = globalInodeIndex;
        }

        public void InPool()
        {

        }

        public void EnterWriteState(SimFileStream fs)
        {
            if (WriteAccessInstance != null)
                throw new SimFSException(ExceptionType.FileWriteAccessAlreadyTaken);
            WriteAccessInstance = fs ?? throw new System.ArgumentNullException(nameof(fs));
        }

        public void ExitWriteState(SimFileStream fs)
        {
            if (WriteAccessInstance == null)
                return;
            if (WriteAccessInstance == fs)
                WriteAccessInstance = null;
            else
                throw new SimFSException(ExceptionType.InvalidFileStream, "exiting write state with the wrong file stream");
        }
    }
}
