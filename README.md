# SimFS

SimFS is a Single File Simulated File System written in C#.
* It's born to store a large number of small files.
* It runs faster and allocates less memory than the actual file system.
* It stores all things into 1 single file.

**Caution：SimFS是为小文件存储而生的，所以有最大文件大小限制，在使用时一定要注意文件大小不要超过限制！**

现有功能：
1. 动态分配磁盘空间
1. 支持文件夹
1. 支持自定义文件扩展属性
1. 使用了Span\<T\>和Memory\<T\>加快运行速度，减少内存开销。

其实，我想使用这个文件系统存放用户的存档文件，只要我将用户的存档拆的足够碎，这样每次用户每个行为产生的存档数据就足够的小，足够能在极短的时间内将它存储下来。
但是由于现代的文件系统的功能非常丰富，在除了读写以外的操作中通常伴随着较高的overhead，如此这般，才会导致读写大量的零碎小文件时的速度总是提不起来。于是我萌生了自己编写虚拟文件系统来避免这些overhead的想法。

SimFS的数据结构上杂糅了各大文件系统的概念，性能上一直在向[GameFramework的VFS](https://github.com/EllanJiang/GameFramework/tree/master/GameFramework/FileSystem)看齐，目前来说，目前这个项目已经进行到一个不错的状态，虽然还有不少可以打磨的空间，但是时间不允许，先进行一波稳定性调优，以后再考虑添加新的功能吧！

# Usage

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
fielbase.CreateParentDirectory("some/dir/file");


filebase.Dispose();
```

# The Most Important Variable: BlockSize

BlockSize参数是创建FileBase对象时至关重要，并且创建以后无法修改的参数，它的大小影响了SimFS诸多地方，以下用表格的方式一一阐述：

| BlockSize        | 128    | 256   | 512    | 1024   | 2048   | 4096   |
| ---------------- | ------ | ----- | ------ | ------ | ------ | ------ |
| Block Group Size | 128KB  | 512KB | 2048KB | 8192KB | 32MB   | 128MB  |
| Blocks per GB*   | 1024   | 2048  | 4096   | 8192   | 16384  | 32768  |
| Inodes per GB*   | 1024   | 2048  | 4096   | 8192   | 16384  | 32768  |
| File Size Limit  | 95KB   | 191KB | 382KB  | 1785KB | 3570KB | 7140KB |

> GB stands for BlockGroup

SimFS限制BlockSize的值必须是2的N次方，且在128~4096之间（大于或小于这个区间的值都不太有意义）。

对于BlockGroup和Inode概念不熟悉的朋友可以只关注文件大小限制。对于一般用户来说，选**1024**作为BlockSize是一个比较合适的选择。

# Customizable File Attributes

由于SimFS是个极简的虚拟文件系统，一般文件系统中那些创建时间、修改时间、访问时间、权限等信息都是没有的，我认为在打开文件之前能获取一些关键的元数据信息还是有必要的。所以设计了Customizable File Attribute功能。

由于设计原因，Attributes数据是和Inode信息放在一起的，所以大小是固定且不宜设置得过大。目前最大限制为32B，按照上述表格，当Attributes的大小是32B且BlockSize是1024时，每个BlockGroup会多占用128KB(8192*32B)的磁盘空间。

我们只需要在创建Filebase时加入attributeSize参数（且以后不可修改）即可：`var filebase = new Filebase("/path/to/file", attributeSize: 32);`

读取和写入Attributes
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

# Performance

这是在Windows机器上使用BenchmarkDotNet在net8.0的环境下运行的相对严谨的测试：

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

# DataStructure

可以移步至专属章节：[DataStructure](DataStructure.md)

# What's more

目前还需继续完善的功能有：
* 磁盘空间延迟分配
* 遇到异常回滚操作，防止文件系统损坏
* 碎片整理功能
