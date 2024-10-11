public class HostFSTest : IFileSystemTest
{
    public void AddFile(string path, byte[] content)
    {
        File.WriteAllBytes(path, content);
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    public void RenameFile(string from, string to)
    {
        if (!File.Exists(from))
            throw new FileNotFoundException();
        if (File.Exists(to))
            File.Delete(to);
        File.Move(from, to);
    }

    public int ReadFile(string path, byte[] buffer)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException();
        using var fs = File.OpenRead(path);
        return fs.Read(buffer);
    }

    public void DeleteAll(string basePath)
    {
        var files = Directory.GetFiles(basePath);
        foreach (var f in files)
        {
            File.Delete(f);
        }
    }

    public void Flush()
    {
    }
}
