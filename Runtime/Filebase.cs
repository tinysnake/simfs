using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace SimFS
{
    public class Filebase : IDisposable
    {
        public Filebase(string filebasePath, ushort blockSize = 512, byte attributeSize = 0, ushort bufferSize = 4096)
        {
            _fsMan = new FSMan(filebasePath, blockSize, attributeSize, bufferSize);
        }

        private readonly FSMan _fsMan;

        public Pooling Pooling => _fsMan.Pooling;

        private void CheckPath(ref ReadOnlySpan<char> path)
        {
            if (path.IsEmpty)
                throw new ArgumentNullException(nameof(path));
            if (path.Length > 1)
            {
                if (path[0] == '/')
                    path = path[1..];
                if (path[^1] == '/')
                    path = path[..^1];
            }
            if (path.IsEmpty)
                throw new ArgumentNullException(nameof(path));
        }

        private ReadOnlySpan<char> GetFileName(ReadOnlySpan<char> path)
        {
            var index = path.LastIndexOf('/');
            if (index >= 0)
                return path[index..];
            return path;
        }

        public SimFSType Exists(ReadOnlySpan<char> path)
        {
            CheckPath(ref path);
            var parentDir = GetParentDirectory(path, out var fileName);
            if (parentDir == null) return SimFSType.Any;
            if (parentDir.HasChild(fileName, out var isDir))
                return isDir ? SimFSType.Directory : SimFSType.File;
            return SimFSType.Any;
        }

        public bool Exists(ReadOnlySpan<char> path, SimFSType fsType = SimFSType.Any)
        {
            CheckPath(ref path);
            var parentDir = GetParentDirectory(path, out var fileName);
            if (parentDir == null) return false;
            return fsType switch
            {
                SimFSType.Any => parentDir.HasChild(fileName, out _),
                SimFSType.File => parentDir.HasFile(fileName),
                SimFSType.Directory => parentDir.HasDirectory(fileName),
                _ => throw new NotSupportedException(fsType.ToString())
            };
        }

        public bool Exists(ReadOnlySpan<char> parentDirPath, ReadOnlySpan<char> path, SimFSType fsType = SimFSType.Any)
        {
            CheckPath(ref path);
            var parentDir = GetDirectory(parentDirPath);
            if (parentDir == null) return false;
            parentDir = GetParentDirectoryRelatively(parentDir, path, out var fileName, out _, false);
            if (parentDir == null) return false;
            return fsType switch
            {
                SimFSType.Any => parentDir.HasChild(fileName, out _),
                SimFSType.File => parentDir.HasFile(fileName),
                SimFSType.Directory => parentDir.HasDirectory(fileName),
                _ => throw new NotSupportedException(fsType.ToString())
            };
        }

        public void Delete(ReadOnlySpan<char> path, SimFSType fsType = SimFSType.Any, bool throwsIfNotExist = false)
        {
            CheckPath(ref path);
            var parentDir = GetParentDirectory(path, out var fileName);
            if (parentDir == null)
            {
                if (throwsIfNotExist)
                    throw new SimFSException(ExceptionType.DirectoryNotFound);
                return;
            }
            var deleted = fsType switch
            {
                SimFSType.Any => parentDir.TryDeleteChild(fileName),
                SimFSType.File => parentDir.TryDeleteFile(fileName),
                SimFSType.Directory => parentDir.TryDeleteDirectory(fileName),
                _ => throw new NotSupportedException(fsType.ToString()),
            };
            if (!deleted && throwsIfNotExist)
                throw new SimFSException(ExceptionType.FileNotFound);
        }

        public void Move(ReadOnlySpan<char> path, ReadOnlySpan<char> targetPath, SimFSType fsType = SimFSType.Any, bool overwrite = true, bool throws = false)
        {
            CheckPath(ref path);
            var fromDir = GetParentDirectory(path, out var fromName);
            if (fromDir == null && throws)
                throw new SimFSException(ExceptionType.DirectoryNotFound);
            if (!fromDir.HasChild(fromName, out _) && throws)
                throw new SimFSException(ExceptionType.FileNotFound);
            var toDir = GetParentDirectory(targetPath, out var toName, true);
            if (!fromDir.TryMoveChild(fromName, toDir, toName, overwrite) && throws)
                throw new SimFSException(ExceptionType.FileAlreadyExists, targetPath.ToString());
        }

        public void Copy(ReadOnlySpan<char> path, ReadOnlySpan<char> targetPath, SimFSType fsType = SimFSType.Any, bool overwrite = true, bool throws = false)
        {
            var fromDir = GetParentDirectory(path, out var fromName);
            if (fromDir == null && throws)
                throw new SimFSException(ExceptionType.DirectoryNotFound, path.ToString());
            if (!fromDir.HasChild(fromName, out var isDir) && throws)
                throw new SimFSException(ExceptionType.FileNotFound, path.ToString());
            var toDir = GetParentDirectory(targetPath, out var toName, out _, !throws);
            if (toDir == null && throws)
                throw new SimFSException(ExceptionType.DirectoryNotFound, targetPath.ToString());
            if (isDir)
            {
                SimDirectory targetDir;
                if (throws)
                    targetDir = toDir.GetDirectory(toName);
                else
                    targetDir = toDir.GetOrCreateDirectory(toName);

                var list = new List<ReadOnlyMemory<char>>();
                var basePaths = SimUtil.Path.PathSegmentsHolder;
                SimDirectory.GetAllChildren(basePaths, fromDir.GetDirectory(fromName), list, SimFSType.File);
                foreach (var relFilePath in list)
                {
                    using var fromFile = OpenFile(fromDir, relFilePath.Span, OpenFileMode.Open, 0, throws);
                    using var toFile = OpenFile(targetDir, relFilePath.Span, OpenFileMode.OpenOrCreate, 0, throws);
                    fromFile.CopyTo(toFile);
                }
            }
            else
            {
                using var fromFile = OpenFile(fromDir, fromName, OpenFileMode.Open, 0, throws);
                using var toFile = OpenFile(toDir, toName, OpenFileMode.OpenOrCreate, 0, throws);
                fromFile.CopyTo(toFile);
            }
        }


        public SimFileStream OpenFile(ReadOnlySpan<char> path, OpenFileMode mode, int fileSize = -1, bool throwsIfNotExists = true)
        {
            CheckPath(ref path);
            var createIfNoExists = mode > OpenFileMode.Open;
            var dir = GetParentDirectory(path, out var fileName, createIfNoExists) ?? throw new SimFSException(ExceptionType.DirectoryNotFound, path.ToString());
            return OpenFile(dir, fileName, mode, fileSize, throwsIfNotExists);
        }

        public SimFileStream OpenFile(ReadOnlySpan<char> basePath, ReadOnlySpan<char> path, OpenFileMode mode, int fileSize = -1, bool throwsIfNotExists = true)
        {
            CheckPath(ref basePath);
            CheckPath(ref path);
            var createIfNoExists = mode > OpenFileMode.Open;
            var dir = GetDirectory(basePath, createIfNoExists) ?? throw new SimFSException(ExceptionType.DirectoryNotFound, path.ToString());
            dir = GetParentDirectoryRelatively(dir, path, out var fileName, out var _, createIfNoExists) ?? throw new SimFSException(ExceptionType.DirectoryNotFound, path.ToString());
            return OpenFile(dir, fileName, mode, fileSize, throwsIfNotExists);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private SimFileStream OpenFile(SimDirectory dir, ReadOnlySpan<char> fileName, OpenFileMode mode, int fileSize, bool throwsIfNotExists)
        {
            SimFileStream fs;
            if (mode > OpenFileMode.Open)
            {
                var blockCount = 1;
                if (fileSize > 0)
                    blockCount = SimUtil.Number.NextMultipleOf(fileSize, _fsMan.Head.BlockSize) / _fsMan.Head.BlockSize;
                fs = dir.GetOrCreateFile(fileName, blockCount);
            }
            else
            {
                if (!dir.TryGetFile(fileName, out fs))
                {
                    if (throwsIfNotExists)
                        throw new SimFSException(ExceptionType.FileNotFound, fileName.ToString());
                    return null;
                }
            }
            if (mode == OpenFileMode.Truncate)
                fs.SetLength(0);
            else if (mode == OpenFileMode.Append)
                fs.Seek(0, SeekOrigin.End);
            return fs;
        }

        public SimFileInfo GetFileInfo(ReadOnlySpan<char> path, bool throwsIfDirNotExist = false)
        {
            CheckPath(ref path);
            var parentDir = GetParentDirectory(path, out var fileName);
            if (parentDir == null)
            {
                if (throwsIfDirNotExist)
                    throw new SimFSException(ExceptionType.FileNotFound, path.ToString());
                return default;
            }

            if (parentDir.HasFile(fileName))
                return parentDir.GetFileInfo(fileName);
            else if (throwsIfDirNotExist)
                throw new SimFSException(ExceptionType.FileNotFound, path.ToString());
            else return default;
        }

        public ReadOnlySpan<byte> ReadFileAttributes(ReadOnlySpan<char> path, bool throwsIfDirNotExist = false)
        {
            var fi = GetFileInfo(path, throwsIfDirNotExist);
            return fi.Exists ? fi.Attributes : default;
        }

        public ReadOnlyMemory<char>[] GetFiles(ReadOnlySpan<char> path, PathKind pathKind = PathKind.Relative, bool topDirectoryOnly = true, bool throwsIfDirNotExist = false)
        {
            CheckPath(ref path);
            var dir = GetDirectory(path, false);
            if (dir == null)
            {
                if (throwsIfDirNotExist)
                    throw new SimFSException(ExceptionType.DirectoryNotFound, path.ToString());
                return Array.Empty<ReadOnlyMemory<char>>();
            }
            return dir.GetFiles(pathKind, topDirectoryOnly);
        }

        public void GetFiles(ReadOnlySpan<char> path, List<ReadOnlyMemory<char>> fileNames, PathKind pathKind = PathKind.Relative, bool topDirectoryOnly = true, bool throwsIfDirNotExist = false)
        {
            CheckPath(ref path);
            var dir = GetDirectory(path, false);
            if (dir == null)
            {
                if (throwsIfDirNotExist)
                    throw new SimFSException(ExceptionType.DirectoryNotFound, path.ToString());
                return;
            }
            dir.GetFiles(fileNames, pathKind, topDirectoryOnly);
        }

        public ReadOnlyMemory<char>[] GetDirectories(ReadOnlySpan<char> path, PathKind pathKind = PathKind.Relative, bool topDirectoryOnly = true, bool throwsIfDirNotExist = false)
        {
            CheckPath(ref path);
            var dir = GetDirectory(path, false);
            if (dir == null)
            {
                if (throwsIfDirNotExist)
                    throw new SimFSException(ExceptionType.DirectoryNotFound, path.ToString());
                return Array.Empty<ReadOnlyMemory<char>>();
            }
            return dir.GetDirectories(pathKind, topDirectoryOnly);
        }

        public void GetDirectories(ReadOnlySpan<char> path, List<ReadOnlyMemory<char>> dirNames, PathKind pathKind = PathKind.Relative, bool topDirectoryOnly = true, bool throwsIfDirNotExist = false)
        {
            CheckPath(ref path);
            var dir = GetDirectory(path, false);
            if (dir == null)
            {
                if (throwsIfDirNotExist)
                    throw new SimFSException(ExceptionType.DirectoryNotFound, path.ToString());
                return;
            }
            dir.GetDirectories(dirNames, pathKind, topDirectoryOnly);
        }

        public void CreateDirectory(ReadOnlySpan<char> path, bool throwsIfAlreadyExists = false)
        {
            GetDirectory(path, out var created);
            if (!created && throwsIfAlreadyExists)
                throw new SimFSException(ExceptionType.DirectoryAlreadyExists);
        }

        public void CreateParentDirectory(ReadOnlySpan<char> path, bool throwsIfAlreadyExists = false)
        {
            GetParentDirectory(path, out _, out var dirCreated);
            if (!dirCreated && throwsIfAlreadyExists)
                throw new SimFSException(ExceptionType.DirectoryAlreadyExists);
        }

        private SimDirectory GetParentDirectory(ReadOnlySpan<char> path, bool createIfNotExist = false) =>
            GetParentDirectory(path, out _, out _, createIfNotExist);
        private SimDirectory GetParentDirectory(ReadOnlySpan<char> path, out ReadOnlySpan<char> fileName, bool createIfNotExist = false) =>
            GetParentDirectory(path, out fileName, out _, createIfNotExist);
        private SimDirectory GetParentDirectory(ReadOnlySpan<char> path, out ReadOnlySpan<char> fileName, out bool dirCreated) =>
            GetParentDirectory(path, out fileName, out dirCreated, true);

        private SimDirectory GetParentDirectory(ReadOnlySpan<char> path, out ReadOnlySpan<char> fileName, out bool dirCreated, bool createIfNotExist) =>
            GetParentDirectoryRelatively(_fsMan.RootDirectory, path, out fileName, out dirCreated, createIfNotExist);

        private SimDirectory GetParentDirectoryRelatively(SimDirectory baseDir, ReadOnlySpan<char> path, out ReadOnlySpan<char> fileName, out bool dirCreated, bool createIfNotExist)
        {
            if (path.IsEmpty)
                throw new ArgumentNullException(nameof(path));
            dirCreated = false;
            fileName = ReadOnlySpan<char>.Empty;
            if (path.IndexOf('/') < 0)
            {
                fileName = path;
                return baseDir;
            }
            Span<Range> ranges = stackalloc Range[24];
            var segmentCount = path.Split(stackalloc char[1] { '/' }, ranges, true);
            if (segmentCount < 1) return baseDir;
            ranges = ranges[..segmentCount];
            fileName = path[ranges[^1]];
            ranges = ranges[..^1];
            foreach (var range in ranges)
            {
                var seg = path[range];
                if (!baseDir.TryGetDirectory(seg, out var subDir))
                {
                    if (createIfNotExist)
                    {
                        baseDir = baseDir.CreateDirectory(seg);
                        dirCreated = true;
                    }
                    else
                        return null;
                }
                else
                    baseDir = subDir;
            }
            return baseDir;
        }

        private SimDirectory GetDirectory(ReadOnlySpan<char> path, bool createIfNotExist = false)
            => GetDirectory(path, out _, createIfNotExist);
        private SimDirectory GetDirectory(ReadOnlySpan<char> path, out bool dirCreated)
            => GetDirectory(path, out dirCreated, true);

        private SimDirectory GetDirectory(ReadOnlySpan<char> path, out bool dirCreated, bool createIfNotExist)
        {
            dirCreated = false;
            if (path.CompareTo("/", StringComparison.Ordinal) == 0)
                return _fsMan.RootDirectory;
            var parent = GetParentDirectory(path, out var dirName, out _, createIfNotExist);
            if (parent == null) return null;
            if (!parent.TryGetDirectory(dirName, out var subDir))
            {
                if (createIfNotExist)
                {
                    subDir = parent.CreateDirectory(dirName);
                    dirCreated = true;
                }
            }
            return subDir;
        }

        public void ClearDirectory(ReadOnlySpan<char> path, bool throwsIfNotExist = false)
        {
            var dir = GetDirectory(path, false);
            if (dir == null)
            {
                if (throwsIfNotExist)
                    throw new SimFSException(ExceptionType.DirectoryNotFound);
            }
            else
                dir.Clear();
        }

        public void WriteAllText(ReadOnlySpan<char> path, ReadOnlySpan<char> text)
        {
            using var fs = OpenFile(path, OpenFileMode.Truncate);
            WriteTextToFile(fs, text);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteTextToFile(SimFileStream fs, ReadOnlySpan<char> text)
        {
            using var bufferHolder = _fsMan.Pooling.RentBuffer(out var buffer, text.Length);
            var encoder = Encoding.UTF8.GetEncoder();
            var complete = false;
            while (!complete)
            {
                encoder.Convert(text, buffer, true, out var charUsed, out var bytesUsed, out complete);
                fs.Write(buffer[..bytesUsed]);
                text = text[charUsed..];
            }
        }

        public void WriteAllBytes(ReadOnlySpan<char> path, ReadOnlySpan<byte> bytes)
        {
            using var fs = OpenFile(path, OpenFileMode.Truncate);
            fs.Write(bytes);
        }

        public void WriteAllLines<T>(ReadOnlySpan<char> path, T lines) where T : IEnumerable<string>
        {
            using var fs = OpenFile(path, OpenFileMode.Truncate);
            using var sw = new StreamWriter(fs);
            foreach (var x in lines)
            {
                sw.WriteLine(x);
            }
        }

        public byte[] ReadAllBytes(ReadOnlySpan<char> path)
        {
            using var fs = OpenFile(path, OpenFileMode.Open);
            var bytes = new byte[fs.Length];
            fs.Read(bytes);
            return bytes;
        }

        public string ReadAllText(ReadOnlySpan<char> path)
        {
            using var fs = OpenFile(path, OpenFileMode.Open);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }

        public string[] ReadAllLine(ReadOnlySpan<char> path)
        {
            using var fs = OpenFile(path, OpenFileMode.Open);
            var lines = new List<string>();
            using var sr = new StreamReader(fs);
            while (!sr.EndOfStream)
            {
                lines.Add(sr.ReadLine());
            }
            return lines.ToArray();
        }

        public void AppendAllText(ReadOnlySpan<char> path, ReadOnlySpan<char> text)
        {
            using var fs = OpenFile(path, OpenFileMode.Append);
            WriteTextToFile(fs, text);
        }

        public void AppendAllLines<T>(ReadOnlySpan<char> path, T lines) where T : IEnumerable<string>
        {
            using var fs = OpenFile(path, OpenFileMode.Append);
            using var sw = new StreamWriter(fs);
            foreach (var x in lines)
            {
                sw.WriteLine(x);
            }
        }

        public void Dispose()
        {
            _fsMan.Dispose();
        }
    }
}
