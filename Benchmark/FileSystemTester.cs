using BenchmarkDotNet.Attributes;

[SimpleJob]
[MemoryDiagnoser]
public class FileSystemTester
{
    private const string HOST_PREFIX = "hostfs/";
    private const int SIZE = 100;
    private const int SEED = 20241001;

    public FileSystemTester()
    {
        if (Directory.Exists(HOST_PREFIX))
            Directory.Delete(HOST_PREFIX, true);
        Directory.CreateDirectory(HOST_PREFIX);
        _random = new Random(SEED);
        for (var i = 0; i < SIZE; i++)
        {
            _fileNames.Add(GetNewName());
            _fileNamesForHost.Add(HOST_PREFIX + _fileNames[i]);
            _newFileNames.Add(GetNewName());
            _newFileNamesForHost.Add(HOST_PREFIX + _newFileNames[i]);
            var fileSize = _random.Next(10112) + 128; // 128B ~ 10240B
            var content = new byte[fileSize];
            _random.NextBytes(content);
            _fileContents.Add(content);
        }
    }

    private readonly List<string> _fileNamesForHost = new(SIZE);
    private readonly List<string> _newFileNamesForHost = new(SIZE);
    private readonly List<string> _fileNames = new(SIZE);
    private readonly List<string> _newFileNames = new(SIZE);
    private readonly List<byte[]> _fileContents = new(SIZE);

    private static IFileSystemTest _hostfs = new HostFSTest();
    private static IFileSystemTest _gameFramework = new GameFramworkTest("test.gff");
    private static IFileSystemTest _simFS = new SimFSTest("test.smfs");
    private byte[] buffer = new byte[10240];
    private Random _random = new Random();
    private uint _baseIdVal = (uint)DateTimeOffset.UtcNow.Ticks;

    public IEnumerable<object?[]> Arguments()
    {
        yield return new object?[] { _hostfs, _fileNamesForHost };
        yield return new object?[] { _gameFramework, _fileNames };
        yield return new object?[] { _simFS, _fileNames };
    }

    public IEnumerable<object?[]> ArgumentsForRename()
    {
        yield return new object?[] { _hostfs, _fileNamesForHost, _newFileNamesForHost };
        yield return new object?[] { _gameFramework, _fileNames, _newFileNames };
        yield return new object?[] { _simFS, _fileNames, _newFileNames };
    }
    public IEnumerable<object?[]> ArgumentsForDeleteAll()
    {
        yield return new object?[] { _hostfs, HOST_PREFIX };
        yield return new object?[] { _gameFramework, "" };
        yield return new object?[] { _simFS, "/" };
    }

    [GlobalSetup(Targets = [nameof(ReadData), nameof(DeleteData), nameof(RenameData), nameof(DeleteAll)])]
    public void SetupForOthers()
    {
        foreach (var arr in Arguments())
        {
            var tester = arr[0] as IFileSystemTest;
            var names = arr[1] as List<string>;
            FillData(tester!, names!);
        }
    }

    [Benchmark]
    [ArgumentsSource(nameof(Arguments))]
    public void FillData(IFileSystemTest tester, List<string> fileNames)
    {
        for (var i = 0; i < SIZE; i++)
        {
            tester.AddFile(fileNames[i], _fileContents[i]);
        }
    }


    [Benchmark]
    [ArgumentsSource(nameof(Arguments))]
    public void ReadData(IFileSystemTest tester, List<string> fileNames)
    {
        for (var i = 0; i < SIZE; i++)
        {
            tester.ReadFile(fileNames[i], buffer);
        }
    }

    [Benchmark]
    [ArgumentsSource(nameof(Arguments))]
    public void DeleteData(IFileSystemTest tester, List<string> fileNames)
    {
        for (var i = 0; i < SIZE; i++)
        {
            tester.DeleteFile(fileNames[i]);
        }
    }

    [Benchmark]
    [ArgumentsSource(nameof(ArgumentsForRename))]
    public void RenameData(IFileSystemTest tester, List<string> fileNames, List<string> newFileNames)
    {
        for (var i = 0; i < SIZE; i++)
        {
            tester.RenameFile(fileNames[i], newFileNames[i]);
        }
    }


    [Benchmark]
    [ArgumentsSource(nameof(ArgumentsForDeleteAll))]
    public void DeleteAll(IFileSystemTest tester, string basePath)
    {
        for (var i = 0; i < SIZE; i++)
        {
            tester.DeleteAll(basePath);
        }
    }

    private string GetNewName()
    {
        return (_baseIdVal++).ToString("X");
    }
}
