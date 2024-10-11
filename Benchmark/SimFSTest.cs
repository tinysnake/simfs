using SimFS;

public class SimFSTest : IFileSystemTest
{
    public SimFSTest(string filePath)
    {
        if (File.Exists(filePath))
            File.Delete(filePath);
        _fb = new Filebase(filePath);
    }

    private readonly Filebase _fb;

    public void AddFile(string path, byte[] content)
    {
        _fb.WriteAllBytes(path, content);
    }

    public void DeleteFile(string path)
    {
        _fb.Delete(path);
    }

    public void RenameFile(string from, string to)
    {
        _fb.Move(from, to);
    }

    public int ReadFile(string path, byte[] buffer)
    {
        using var fs = _fb.OpenFile(path, OpenFileMode.Open);
        return fs.Read(buffer);
    }

    public void DeleteAll(string basePath)
    {
        var files = _fb.GetFiles(basePath, PathKind.Absolute);
        foreach (var file in files)
        {
            _fb.Delete(file.Span);
        }
    }

    public void Flush()
    {
        _fb.Flush();
    }
}
