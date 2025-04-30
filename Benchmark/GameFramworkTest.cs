using GameFramework.FileSystem;
using System.Reflection;

public class GameFramworkTest : IFileSystemTest
{
    public GameFramworkTest(string filePath)
    {
        if (File.Exists(filePath))
            File.Delete(filePath);
        _stream = new CommonFileSystemStream(filePath, FileSystemAccess.ReadWrite, true);
        var asm = Assembly.GetAssembly(typeof(IFileSystem));
        _fs = (asm!.GetType("GameFramework.FileSystem.FileSystem")!.GetMethod("Create", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, new object[]{filePath, FileSystemAccess.ReadWrite,
            _stream, 1000, 8000}) as IFileSystem)!;
    }

    private readonly CommonFileSystemStream _stream;
    private readonly IFileSystem _fs;

    public void AddFile(string path, byte[] content)
    {
        _fs.WriteFile(path, content);
    }

    public void DeleteFile(string path)
    {
        _fs.DeleteFile(path);
    }

    public int ReadFile(string path, byte[] buffer)
    {
        if (!_fs.HasFile(path))
            throw new FileNotFoundException();
        return _fs.ReadFile(path, buffer);
    }

    public void RenameFile(string from, string to)
    {
        _fs.RenameFile(from, to);
    }

    public void DeleteAll(string _)
    {
        var files = _fs.GetAllFileInfos();
        foreach (var file in files)
        {
            _fs.DeleteFile(file.Name);
        }
    }

}
