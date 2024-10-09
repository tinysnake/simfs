# SimFS

SimFS is a Single File Simulated File System written in C#.
* It's born to store a large number of small files.
* It runs faster and allocates less memory than the actual file system.

现有功能：
1. 动态分配磁盘空间
2. 支持文件夹
3. 使用了Span\<T\>和Memory\<T\>加快运行速度，减少内存开销。

其实，我想使用这个文件系统存放用户的存档文件，只要我将用户的存档拆的足够碎，这样每次用户每个行为产生的存档数据就足够的小，足够能在极短的时间内将它存储下来。
但是由于现代的文件系统的功能非常丰富，导致它们在读写大量的零碎小文件时的速度时完全不及读写单个大文件的速度。于是我萌生了自己编写虚拟文件系统的想法。
SimFS的数据结构上杂糅了各大文件系统的概念，性能上一直在向[GameFramework的VFS](https://github.com/EllanJiang/GameFramework/tree/master/GameFramework/FileSystem)看齐，目前来说，目前这个项目已经进行到一个不错的状态，虽然还有不少可以打磨的空间，但是时间不允许，先进行一波稳定性调优，以后再考虑添加新的功能吧（如果有必要的话）！

目前还想继续开发的功能有：
* 磁盘空间延迟分配
* 遇到异常回滚操作，防止文件系统损坏
