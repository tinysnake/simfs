public interface IFileSystemTest
{
    void AddFile(string path, byte[] content);
    void DeleteFile(string path);
    void RenameFile(string from, string to);
    int ReadFile(string path, byte[] buffer);
    void DeleteAll(string basePath);
}
