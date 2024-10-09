namespace SimFS
{
    public enum OpenFileMode
    {
        /// <summary>
        /// only trys to open a file
        /// </summary>
        Open,
        /// <summary>
        /// will create file if it doesn't exists
        /// </summary>
        OpenOrCreate,
        /// <summary>
        /// base on OpenOrCreate, it will reset the file content once it's opened
        /// </summary>
        Truncate,
        /// <summary>
        /// base on OpenOrCreate, it will Seek to the end of the file once it's opened
        /// </summary>
        Append
    }
}
