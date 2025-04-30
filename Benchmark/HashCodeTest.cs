using BenchmarkDotNet.Attributes;

[SimpleJob]
[MemoryDiagnoser]
public class HashCodeTest
{
    public HashCodeTest()
    {
        _intDict = new()
        {
            { typeof(GameFramworkTest).GetHashCode(), nameof(GameFramworkTest) },
            { typeof(HostFSTest).GetHashCode(), nameof(HostFSTest) },
            { typeof(SimFSTest).GetHashCode(), nameof(SimFSTest) },
            { typeof(FileSystemTester).GetHashCode(), nameof(FileSystemTester) },
            { typeof(IFileSystemTest).GetHashCode(), nameof(IFileSystemTest) },
            { typeof(Program).GetHashCode(), nameof(Program) },
        };
        _typeDict = new()
        {
            { typeof(GameFramworkTest), nameof(GameFramworkTest) },
            { typeof(HostFSTest), nameof(HostFSTest) },
            { typeof(SimFSTest), nameof(SimFSTest) },
            { typeof(FileSystemTester), nameof(FileSystemTester) },
            { typeof(IFileSystemTest), nameof(IFileSystemTest) },
            { typeof(Program), nameof(Program) },
        };
        TType = [typeof(SimFSTest)];
        TInt = [TType[0].GetHashCode()];
    }
    private readonly Dictionary<int, string> _intDict;
    private readonly Dictionary<Type, string> _typeDict;
    public object[] TInt { get; }
    public object[] TType { get; }


    [Benchmark]
    [ArgumentsSource(nameof(TType))]
    public string? IntTest(Type t)
    {
        if (_intDict.TryGetValue(t.GetHashCode(), out var v))
            return v;
        return null;
    }

    [Benchmark]
    [ArgumentsSource(nameof(TType))]
    public string? TypeTest(Type t)
    {
        if (_typeDict.TryGetValue(t, out var v))
            return v;
        return null;
    }
}
