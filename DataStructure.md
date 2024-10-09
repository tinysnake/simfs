本文介绍SimFS的数据结构

# SimFS Structure

| FSHead | Block Group 0 | Block Group 1 | Block Group N ... |
| ------ | ------------- | ------------- | ----------------- |

整个文件系统包含一个FSHead和无数个BlockGroup，BlockGroup的数量是根据需要进行创建的。默认有一个BlockGroup。

# FSHead

FSHead定长256B，它的结构为：

| Name                 | Type or Length | Description |
| -------------------- | -------------- | ----------- |
| Signature            | 4              | String constant: SMFS |
| Version              | byte           |             |
| Block Size           | ushort         |             |
| Block Pointers Count | byte           | Determins how many blocks pointers can a inode have |
| Attribute Size       | byte           | User defined the size of custom file attributes |
| Block Group Count    | int            | Tells the file system how many Block Groups are allocated.
| ...                  |                | Reserved    |


# BlockGroup Structure

| Structure | Block Group Head | Blocks Bitmap  | Inodes Bitmap  | InodeTable                 | Blocks ...                 |
| --------- | ---------------- | -------------- | -------------- | -------------------------- | -------------------------- | 
| Size      | 32B              | BlockSize * 1B | BlockSize * 1B | InodeSize * BlockSize * 8B | BlockSize * BlockSize * 8B |


BlockGroup是根据BlockSize计算出来的定长的数据结构, 具体每个区块大小的计算方式看上述表格。

我使用了BlockGroup结构来分配磁盘内存，如此可以根据用户的需要逐渐分配和扩张磁盘空间。
每个数据块的作用会在底下一一解释。

这里先笼统的概括一下SimFS是如何保存文件的，后面会再展开：
1. 文件的元数据保存在inode中。
1. 文件的内容保存在blocks中。

## Block Group Head

Block Group Head数据定长32B，它的结构为：

| Name                 | Type or Length | Description |
| -------------------- | -------------- | ----------- |
| Signature            | 4              | String constant: BKGP |
| Free Blocks          | ushort         | Tells the file system how many free blocks can allocate, without loading it |
| Free Inodes          | ushort         | Tells the file system how many free inodes can allocate, without loading it |
| ...                  |                | Reserved    |

## Blocks Bitmap & Inodes Bitmap

这了个Bitmap用来追踪Block/Inode的分配情况，可以追踪`BlockSize * 8`个Block/Inode。
BlockGroup会根据这两个Bitmap来分配空闲的Inode和Block给对应的文件。

## InodeTable

Inode的大小比较复杂，它和BlockSize和AttributeSize有关：

| Name           | Type or Length       | Description |
| -------------- | -------------------- | ----------- |
| Length         | int                  | Length of the file |
| Usage          | byte                 | a value to check usage of a inode, more info on InodeUsage Enum |
| Attributes     | User Defined         | The place to store user defined custom attributes |
| Block Pointers | BlockPointerData * N | BlockPointerData is a structure that tells the file system were the content of the is |

### Block Pointer Data

Block Pointer Data是一个连续的Block指针，代表了一段连续的磁盘空间。

| Name           | Type or Length | Description |
| -------------- | -------------- | ----------- |
| Block Index    | int            | The global index of a block |
| Block Count    | byte           | The contiguous size of a allocated block pointer |

一个Inode包含N个BlockPointerData，N的值为当BlockSize小于1024是为3，否则为7。这直接定义了一个文件的最大限制。
* 当BlockSize为512时，N为3，那么文件的最大大小为：`512 * byte.MaxValue * 3 = 382.5KB`
* 当BlockSize为1024时，N为7，那么文件的最大大小为：`1024 * byte.MaxValue * 7 = 1785KB`

## Inode Size

说回Inode的大小，它的大小为:

> * 5i：Inode的基础参数大小
> * 5p：BlockPointerData的大小
> * N: BlockPointerData的数量
> * AttrSize : 用户自定义Attribute的大小

即：`5 + 5 * N + AttrSize`。

### Inode Table Size
根据开头的描述，一个BlockGroup会包含8 * BlockSize个Inode，所以Inode Table的大小也是固定的： `InodeSize * BlockSize * 8`

# File Allocation

一个文件的创建先要从BlockGroup中分配一个Inode，然后根据文件的大小动态分配BlockPointerData并保存在Inode中，根据Inode中的BlockPointerData数据来定位可以读取或写入的起始位置和长度。

# Directory

和Linux系统一样，文件夹是一个特殊的文件，所以它文件夹的数据也保存在Inode中。
并且定义：第0个BlockGroup的第0个Inode为根目录的Inode，根目录不可删除。

一个Directory内包含了N个Entry,N的数量也是有上线的，为：`文件大小上限/EntrySize`。但是Entry的大小也是动态的。
先展示一下DirectoryEntry的数据结构：

| Name           | Type or Length | Description |
| -------------- | -------------- | ----------- |
| Length         | byte           | the entire length of a entry |
| Name Length    | byte           | the length of the filename string |
| InodeUsage     | byte           | to check the entry is a file or a directory |
| Inode Index    | int            | the global index of the actual inode |
| Name Bytes     | Length - 7     | the bytes to store the filename |

由此可以看出，一个DirectoryEntry的最大长度是255，其次，这个Entry包含的文件名最大可以存放248个byte,即文件名在进行UTF-8编码以后最大的长度是248。

DirectoryEntry在分配以后，若该文件被删除，它的Entry也不会回收（以后会在碎片整理中回收），当需要创建一个新的文件，且它的文件名长度小于`EntryLength - 7`时则会被重复利用。
