# SimFS

SimFS 是一个用 C# 编写的单文件虚拟文件系统（Virtual File System)。
* 它专为存储大量小文件而设计。
* 与实际文件系统相比，它运行速度更快，内存分配更少。
* 它将所有数据存储在一个单独的文件中。

**现有功能：**
1. 动态分配磁盘空间
2. 支持文件夹
3. 支持自定义文件扩展属性
4. 使用 `Span<T>` 和 `Memory<T>` 减少了内存的开销
5. 存储事务（Transaction），拥有一次性回滚改动的能力。

**注意事项：**
1. SimFS是线程不安全的，所以只能通过**一个线程**来访问SimFS的API（至于该线程是不是主线程并不重要）。
2. 请勿在一个文件写入完成前去读取它，否则会有异常抛出。
3. 当前版本的SimFS，在存储事务提交或回滚前，所有的改动都是暂存在内存当中的，所以在创建大型的存储事务前请注意内存的使用情况。
4. 文件大小是被Filebase的`BlockSize`参数限制的，具体信息可以跳转到BlockSize相关段落查看。
5. 最大的文件数量也被`BlockSize`限制的，所以如果你想保存海量的文件到一个文件夹中，你最好将他们分类保存在各种不同的子文件夹中。

**创作SimFS的起因：**

在传统的做法中，我们会把玩家的存档一股脑全部写入到一个文件当中，即便玩家只改动了很小的一部分内容。序列化存档消耗时间，文件的IO读写也需要时间。于是我想：如果我把存档拆到极致的小，那么序列化和文件IO的时间也会小到忽略不计。

然而，现代文件系统功能丰富，除读写操作外，其他操作通常会带来一些额外的开销。这就是为什么读写大量零碎小文件的速度总是比读写单个大文件慢的原因。因此，我萌生了编写自己的虚拟文件系统来解决小文件的读写速度慢的问题。

SimFS 的数据结构融合了多种文件系统的概念，在性能上一直对标 [GameFramework 的 VFS](https://github.com/EllanJiang/GameFramework/tree/master/GameFramework/FileSystem)。目前，该项目已取得不错的进展。虽然仍有许多可优化的空间，但由于时间有限，我将先进行稳定性优化，后续再考虑添加新功能。

# 使用方法

```csharp
using SimFS;

var blockSize = 1024;
var attributeSize = 0;
var bufferSize = 8196;

var filebase = new Filebase("/path/to/file", blockSize, attributeSize, bufferSize);
// var filebase = new Filebase(File.Open("/path/to/file"), blockSize, attributeSize, bufferSize);

using var fs = filebase.OpenFile("some/file", OpenFileMode.OpenOrCreate);
// fs inherits from System.IO.Stream

filebase.WriteAllText("some/file", "abc");
filebase.WriteAllLines("some/file", new []{ "abc", "def"});
filebase.WriteAllBytes("some/file", new byte[] {0, 1, 2, 3});
filebase.AppendAllText(...);
filebase.AppendAllLines(...);
var bytes = filebase.ReadAllBytes("some/file");
var text = filebase.ReadAllText("some/file");
var lines = filebase.ReadAllLines("some/file");
byte[] attr = filebase.ReadFileAttributes("some/file");

SimFileInfo fi = filebase.GetFileInfo("some/file");

var files = filebase.GetFiles("some/directory", PathKind.Relative, topDirectoryOnly: true);
var dirs = filebase.GetFiles("some/directory", PathKind.Relative, topDirectoryOnly: true);

if(filebase.Exists("some/file", SimFSType.File)) { }
if(filebase.Exists("some/directory", SimFSType.Directory)) { }
if(filebase.Exists("some/path", SimFSType.Any)) { }

filebase.Move("some/file1", "some/file2");
filebase.Copy("some/file1", "some/file2");
filebase.Delete("some/file");

filebase.CreateDirectory("some/dir");
filebase.CreateParentDirectory("some/dir/file");

filebase.Dispose();
```

# 最重要的变量：块大小（BlockSize）

`BlockSize` 参数在创建 `FileBase` 对象时至关重要，且创建后**无法修改**。其大小会对 SimFS 的多个方面产生影响，以下表格详细说明：

| 块大小（BlockSize）      | 128    | 256   | 512    | 1024   | 2048   | 4096   |
| ---------------------- | ------ | ----- | ------ | ------ | ------ | ------ |
| 块组大小                | 128KB  | 512KB | 2048KB | 8192KB | 32MB   | 128MB  |
| 每个块组包含的块数量      | 1024   | 2048  | 4096   | 8192   | 16384  | 32768  |
| 每个块组包含的索引节点数量 | 1024   | 2048  | 4096   | 8192   | 16384  | 32768  |
| 文件大小限制             | 95KB   | 191KB | 382KB  | 1785KB | 3570KB | 7140KB |


SimFS 限制 `BlockSize` 的值必须是 2 的幂次方，且范围在 128 到 4096 之间（超出此范围的值意义不大）。

对于不熟悉“块组（BlockGroup）”和“索引节点（Inode）”概念的用户，可以重点关注文件大小限制。对于普通用户来说，选择 **1024** 作为 `BlockSize` 是一个不会错的选择。

# 可定制文件属性（Attributes）

由于 SimFS 是一个极简的虚拟文件系统，它没有一般文件系统中常见的创建时间、修改时间、访问时间和权限等信息。我认为在打开文件之前获取一些关键的元数据信息是有必要的，因此设计了可定制文件Attributes功能。

由于设计原因，`Attributes` 数据与 `Inode` 信息存储在一起，因此其大小是固定的，不宜设置得过大。目前最大限制为 32B。根据上述表格，当 `Attributes` 大小为 32B 且 `BlockSize` 为 1024 时，每个 `BlockGroup` 将额外占用 128KB（8192 * 32B）的磁盘空间。

我们只需在创建 `Filebase` 时添加 `attributeSize` 参数（且后续**无法修改**）即可：`var filebase = new Filebase("/path/to/file", attributeSize: 32);`

读取和写入 `Attributes`：
```
byte[] attrBytes = filebase.GetFileAttributes("some/file");
SimFileInfo fi = filebase.GetFileInfo("some/file");
ReadOnlySpan<byte> attrBytes1 = fi.Attributes;

// We can change Attributes by open the file:
using var fs = fi.Open();
fs.WriteAttribute(new byte[] {1, 2, 3, 4});

var buffer = new byte[32];
fs.ReadAttribute(buffer);
```

# 性能

这是在 Windows 机器上使用 BenchmarkDotNet 在 .NET 8.0 环境下进行的相对严谨的测试：

```
// * Summary *

BenchmarkDotNet v0.14.0, Windows 11 (10.0.22631.4317/23H2/2023Update/SunValley3)
12th Gen Intel Core i7-12700, 1 CPU, 20 logical and 12 physical cores
.NET SDK 8.0.403
  [Host]     : .NET 8.0.10 (8.0.1024.46610), X64 RyuJIT AVX2 [AttachedDebugger]
  DefaultJob : .NET 8.0.10 (8.0.1024.46610), X64 RyuJIT AVX2
```

| Method     | tester           | Mean           | Error         | StdDev        | Gen0   | Allocated |
|----------- |----------------- |---------------:|--------------:|--------------:|-------:|----------:|
| RenameData | HostFSTest       |             NA |            NA |            NA |     NA |        NA |
| RenameData | GameFramworkTest |     1,157.3 ns |       8.37 ns |       7.83 ns |      - |         - |
| RenameData | SimFSTest        |    12,321.9 ns |     112.23 ns |     104.98 ns |      - |         - |
| FillData   | HostFSTest       | 9,659,176.5 ns | 273,644.56 ns | 806,847.22 ns |      - |   36810 B |
| FillData   | GameFramworkTest |   432,373.4 ns |   5,458.10 ns |   5,105.51 ns | 0.4883 |   11726 B |
| FillData   | SimFSTest        |   184,853.6 ns |     826.91 ns |     733.03 ns |      - |         - |
| ReadData   | HostFSTest       | 1,288,943.2 ns |   8,672.89 ns |   7,688.29 ns | 5.8594 |   83203 B |
| ReadData   | GameFramworkTest |    97,419.0 ns |     463.14 ns |     410.56 ns |      - |         - |
| ReadData   | SimFSTest        |   132,595.6 ns |     586.29 ns |     519.73 ns |      - |         - |
| DeleteData | HostFSTest       |   231,807.6 ns |     943.01 ns |     882.10 ns | 2.1973 |   29600 B |
| DeleteData | GameFramworkTest |       587.9 ns |       3.55 ns |       3.15 ns |      - |         - |
| DeleteData | SimFSTest        |     4,849.4 ns |      42.58 ns |      37.74 ns |      - |         - |

此外，我使用Unity引擎在不同的移动设备上进行也进行了不严谨的测试：

Environment: Unity 2022.4.33f1 - Release
ScriptBackend: IL2CPP

Test Device: XiaoMi MI5

| Method               | tester           | Mean           |
|--------------------- |----------------- |---------------:|
| FillData-FirstTime   | HostFSTest       |          92 ms |
| FillData-FirstTime   | GameFramworkTest |         304 ms |
| FillData-FirstTime   | SimFSTest        |         216 ms |
| FillData             | HostFSTest       |          65 ms |
| FillData             | GameFramworkTest |          26 ms |
| FillData             | SimFSTest        |           7 ms |
| ReadData             | HostFSTest       |          59 ms |
| ReadData             | GameFramworkTest |          24 ms |
| ReadData             | SimFSTest        |          34 ms |
| DeleteData           | HostFSTest       |          24 ms |
| DeleteData           | GameFramworkTest |          15 ms |
| DeleteData           | SimFSTest        |           2 ms |


Test Device: Google Pixel 5

| Method               | tester           | Mean           |
|--------------------- |----------------- |---------------:|
| FillData-FirstTime   | HostFSTest       |          27 ms |
| FillData-FirstTime   | GameFramworkTest |         127 ms |
| FillData-FirstTime   | SimFSTest        |          88 ms |
| FillData             | HostFSTest       |          20 ms |
| FillData             | GameFramworkTest |           7 ms |
| FillData             | SimFSTest        |           2 ms |
| ReadData             | HostFSTest       |          17 ms |
| ReadData             | GameFramworkTest |           1 ms |
| ReadData             | SimFSTest        |           2 ms |
| DeleteData           | HostFSTest       |           8 ms |
| DeleteData           | GameFramworkTest |           5 ms |
| DeleteData           | SimFSTest        |           1 ms |

# 数据结构（DataStructure）

您可以查看专属章节：[数据结构（DataStructure）](DataStructure.md)

# 后续计划

目前仍需完善的功能包括：
- [x] 磁盘空间延迟分配
- [x] 异常回滚操作，防止文件系统损坏
- [ ] 碎片整理功能
