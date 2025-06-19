using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace SimFS
{
    public class Filebase : IDisposable
    {
        public Filebase(string filebasePath, ushort blockSize = 1024, byte attributeSize = 0, Customizer customizer = null)
        {
            var dir = Path.GetDirectoryName(filebasePath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var fs = File.Open(filebasePath, FileMode.OpenOrCreate);
            _fsMan = new FSMan(fs, blockSize, attributeSize, customizer);
        }

        public Filebase(Stream stream, ushort blockSize = 1024, byte attributeSize = 0, Customizer customizer = null)
        {
            _fsMan = new FSMan(stream, blockSize, attributeSize, customizer);
        }

        private readonly FSMan _fsMan;

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

        private EnsureTransaction EnsureTransaction(Transaction t, TransactionMode mode = TransactionMode.Immediate) => new(t, _fsMan, mode);

        public Transaction BeginTransaction(string friendlyName = null)
        {
            return _fsMan.BeginTransaction(TransactionMode.Manual, friendlyName);
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

        public bool Exists(ReadOnlySpan<char> path, SimFSType fsType)
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
            parentDir = GetParentDirectoryRelatively(null, parentDir, path, out var fileName, out _, false);
            if (parentDir == null) return false;
            return fsType switch
            {
                SimFSType.Any => parentDir.HasChild(fileName, out _),
                SimFSType.File => parentDir.HasFile(fileName),
                SimFSType.Directory => parentDir.HasDirectory(fileName),
                _ => throw new NotSupportedException(fsType.ToString())
            };
        }

        public void Delete(ReadOnlySpan<char> path, SimFSType fsType = SimFSType.Any, Transaction transaction = null, bool throwsIfNotExist = false)
        {
            CheckPath(ref path);
            var parentDir = GetParentDirectory(path, out var fileName);
            if (parentDir == null)
            {
                if (throwsIfNotExist)
                    throw new SimFSException(ExceptionType.DirectoryNotFound);
                return;
            }
            using var et = EnsureTransaction(transaction);
            var deleted = fsType switch
            {
                SimFSType.Any => parentDir.TryDeleteChild(et, fileName),
                SimFSType.File => parentDir.TryDeleteFile(et, fileName),
                SimFSType.Directory => parentDir.TryDeleteDirectory(et, fileName),
                _ => throw new NotSupportedException(fsType.ToString()),
            };
            if (!deleted && throwsIfNotExist)
                throw new SimFSException(ExceptionType.FileNotFound);
        }


        public void Delete(SimFileInfo file, Transaction transaction = null, bool throwsIfNotExist = false)
        {
            var parentDir = file.ParentDirectory;
            if (parentDir == null)
            {
                if (throwsIfNotExist)
                    throw new SimFSException(ExceptionType.DirectoryNotFound);
                return;
            }
            using var et = EnsureTransaction(transaction);
            var deleted = parentDir.TryDeleteFile(transaction, file.Name);
            if (!deleted && throwsIfNotExist)
                throw new SimFSException(ExceptionType.FileNotFound);
        }


        public void Delete(SimDirectoryInfo dir, Transaction transaction = null, bool throwsIfNotExist = false)
        {
            var curDir = dir.GetDirectory(throwsIfNotExist);
            var parentDir = curDir.Parent;
            if (parentDir == null)
            {
                if (throwsIfNotExist)
                    throw new SimFSException(ExceptionType.DirectoryNotFound);
                return;
            }
            using var et = EnsureTransaction(transaction);
            var deleted = parentDir.TryDeleteFile(transaction, dir.Name);
            if (!deleted && throwsIfNotExist)
                throw new SimFSException(ExceptionType.FileNotFound);
        }

        public void Move(ReadOnlySpan<char> path, ReadOnlySpan<char> targetPath, SimFSType fsType = SimFSType.Any, bool overwrite = true, Transaction transaction = null, bool throws = false)
        {
            CheckPath(ref path);
            var fromDir = GetParentDirectory(path, out var fromName);
            if (fromDir == null && throws)
                throw new SimFSException(ExceptionType.DirectoryNotFound);
            if (!fromDir.HasChild(fromName, out _) && throws)
                throw new SimFSException(ExceptionType.FileNotFound);
            var et = EnsureTransaction(transaction);
            var success = false;
            try
            {
                var toDir = GetParentDirectory(et, targetPath, out var toName, true);
                if (!fromDir.TryMoveChild(et, fromName, toDir, toName, overwrite) && throws)
                    throw new SimFSException(ExceptionType.FileAlreadyExists, targetPath.ToString());
                success = true;
            }
            finally
            {
                et.Complete(success);
            }
        }

        public void Move(SimFileInfo fromInfo, ReadOnlySpan<char> targetPath, bool overwrite = true, Transaction transaction = null, bool throws = false)
        {
            if (!fromInfo.Exists)
            {
                if (throws)
                    throw new SimFSException(ExceptionType.FileNotFound, fromInfo.Name.ToString());
                return;
            }
            var fromDir = fromInfo.ParentDirectory;
            if (fromDir == null && throws)
                throw new SimFSException(ExceptionType.DirectoryNotFound);
            var fromName = fromInfo.Name;
            var et = EnsureTransaction(transaction);
            var success = false;
            try
            {
                var toDir = GetParentDirectory(et, targetPath, out var toName, true);
                if (!fromDir.TryMoveChild(et, fromName, toDir, toName, overwrite) && throws)
                    throw new SimFSException(ExceptionType.FileAlreadyExists, targetPath.ToString());
                success = true;
            }
            finally
            {
                et.Complete(success);
            }
        }

        public void Move(SimDirectoryInfo fromInfo, ReadOnlySpan<char> targetPath, bool overwrite = true, Transaction transaction = null, bool throws = false)
        {
            var dir = fromInfo.GetDirectory(throws);
            var fromName = fromInfo.Name;
            var fromDir = dir.Parent;
            var et = EnsureTransaction(transaction);
            var success = false;
            try
            {
                var toDir = GetParentDirectory(et, targetPath, out var toName, true);
                if (!fromDir.TryMoveChild(et, fromName, toDir, toName, overwrite) && throws)
                    throw new SimFSException(ExceptionType.FileAlreadyExists, targetPath.ToString());
                success = true;
            }
            finally
            {
                et.Complete(success);
            }
        }

        public void Copy(ReadOnlySpan<char> path, ReadOnlySpan<char> targetPath, bool overwrite = true, Transaction transaction = null, bool throws = false)
        {
            var fromDir = GetParentDirectory(path, out var fromName);
            if (fromDir == null && throws)
                throw new SimFSException(ExceptionType.DirectoryNotFound, path.ToString());
            if (!fromDir.HasChild(fromName, out var isDir) && throws)
                throw new SimFSException(ExceptionType.FileNotFound, path.ToString());
            var et = EnsureTransaction(transaction);
            var success = false;
            try
            {
                var toDir = GetParentDirectory(et, targetPath, out var toName, out _, !throws);
                if (toDir == null && throws)
                    throw new SimFSException(ExceptionType.DirectoryNotFound, targetPath.ToString());
                if (isDir)
                {
                    if (toDir.HasDirectory(toName) && !overwrite)
                    {
                        if (throws)
                            throw new SimFSException(ExceptionType.DirectoryAlreadyExists, targetPath.ToString());
                        return;
                    }
                    SimDirectory targetDir;
                    if (throws)
                        targetDir = toDir.GetDirectory(toName);
                    else
                        targetDir = toDir.GetOrCreateDirectory(et, toName);


                    var list = new List<ReadOnlyMemory<char>>();
                    var basePaths = SimUtil.Path.PathSegmentsHolder;
                    SimDirectory.GetAllChildren(basePaths, fromDir.GetDirectory(fromName), list, SimFSType.File);
                    foreach (var relFilePath in list)
                    {
                        using var fromFile = OpenFile(et, fromDir, relFilePath.Span, OpenFileMode.Open, 0, throws);
                        using var toFile = OpenFile(et, targetDir, relFilePath.Span, OpenFileMode.OpenOrCreate, 0, throws);
                        fromFile.CopyTo(toFile);
                    }
                }
                else
                {
                    if (toDir.HasFile(toName) && !overwrite)
                    {
                        if (throws)
                            throw new SimFSException(ExceptionType.FileAlreadyExists, targetPath.ToString());
                        return;
                    }
                    using var fromFile = OpenFile(et, fromDir, fromName, OpenFileMode.Open, 0, throws);
                    using var toFile = OpenFile(et, toDir, toName, OpenFileMode.OpenOrCreate, 0, throws);
                    fromFile.CopyTo(toFile);
                }
                success = true;
            }
            finally
            {
                et.Complete(success);
            }
        }

        public void Copy(SimFileInfo fromInfo, ReadOnlySpan<char> targetPath, SimFSType fsType = SimFSType.Any, bool overwrite = true, Transaction transaction = null, bool throws = false)
        {
            if (!fromInfo.Exists)
            {
                if (throws)
                    throw new SimFSException(ExceptionType.FileNotFound, fromInfo.Name.ToString());
            }
            var et = EnsureTransaction(transaction);
            var success = false;
            try
            {
                var toDir = GetParentDirectory(et, targetPath, out var toName, out _, !throws);
                if (toDir == null && throws)
                    throw new SimFSException(ExceptionType.DirectoryNotFound, targetPath.ToString());
                if (toDir.HasFile(toName) && !overwrite)
                {
                    if (throws)
                        throw new SimFSException(ExceptionType.FileAlreadyExists, targetPath.ToString());
                    return;
                }
                using var fromFile = fromInfo.Open(throws);
                using var toFile = OpenFile(et, toDir, toName, OpenFileMode.OpenOrCreate, 0, throws);
                fromFile.CopyTo(toFile);
                success = true;
            }
            finally
            {
                et.Complete(success);
            }
        }

        public void Copy(SimDirectoryInfo dir, ReadOnlySpan<char> targetPath, SimFSType fsType = SimFSType.Any, bool overwrite = true, Transaction transaction = null, bool throws = false)
        {
            var fromDir = dir.GetDirectory(throws);
            var et = EnsureTransaction(transaction);
            var success = false;
            try
            {
                var toDir = GetParentDirectory(et, targetPath, out var toName, out _, !throws);
                if (toDir == null && throws)
                    throw new SimFSException(ExceptionType.DirectoryNotFound, targetPath.ToString());
                if (toDir.HasDirectory(toName) && !overwrite)
                {
                    if (throws)
                        throw new SimFSException(ExceptionType.DirectoryAlreadyExists, targetPath.ToString());
                    return;
                }
                SimDirectory targetDir;
                if (throws)
                    targetDir = toDir.GetDirectory(toName);
                else
                    targetDir = toDir.GetOrCreateDirectory(et, toName);


                var list = new List<ReadOnlyMemory<char>>();
                var basePaths = SimUtil.Path.PathSegmentsHolder;
                SimDirectory.GetAllChildren(basePaths, fromDir, list, SimFSType.File);
                foreach (var relFilePath in list)
                {
                    using var fromFile = OpenFile(et, fromDir, relFilePath.Span, OpenFileMode.Open, 0, throws);
                    using var toFile = OpenFile(et, targetDir, relFilePath.Span, OpenFileMode.OpenOrCreate, 0, throws);
                    fromFile.CopyTo(toFile);
                }
                success = true;
            }
            finally
            {
                et.Complete(success);
            }
        }

        public SimFileStream OpenFile(ReadOnlySpan<char> path, OpenFileMode mode, Transaction transaction = null, int fileSize = -1, bool throwsIfNotExists = true)
        {
            CheckPath(ref path);
            var createIfNoExists = mode > OpenFileMode.Open;
            var et = createIfNoExists ? EnsureTransaction(transaction, TransactionMode.Temproary) : default;
            var dir = GetParentDirectory(et, path, out var fileName, createIfNoExists) ?? throw new SimFSException(ExceptionType.DirectoryNotFound, path.ToString());
            return OpenFile(et, dir, fileName, mode, fileSize, throwsIfNotExists);
        }

        public SimFileStream OpenFile(SimDirectoryInfo parentDir, ReadOnlySpan<char> fileName, OpenFileMode mode, Transaction transaction = null, int fileSize = -1, bool throwsIfNotExists = true)
        {
            var dir = parentDir.GetDirectory(throwsIfNotExists);
            var createIfNoExists = mode > OpenFileMode.Open;
            var et = createIfNoExists ? EnsureTransaction(transaction, TransactionMode.Temproary) : default;
            return OpenFile(et, dir, fileName, mode, fileSize, throwsIfNotExists);
        }

        public SimFileStream OpenFile(ReadOnlySpan<char> basePath, ReadOnlySpan<char> path, OpenFileMode mode, Transaction transaction = null, int fileSize = -1, bool throwsIfNotExists = true)
        {
            CheckPath(ref basePath);
            CheckPath(ref path);
            var createIfNoExists = mode > OpenFileMode.Open;
            var et = createIfNoExists ? EnsureTransaction(transaction, TransactionMode.Temproary) : default;
            var dir = GetDirectory(et, basePath, createIfNoExists) ?? throw new SimFSException(ExceptionType.DirectoryNotFound, path.ToString());
            dir = GetParentDirectoryRelatively(et, dir, path, out var fileName, out var _, createIfNoExists) ?? throw new SimFSException(ExceptionType.DirectoryNotFound, path.ToString());
            return OpenFile(et, dir, fileName, mode, fileSize, throwsIfNotExists);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private SimFileStream OpenFile(Transaction transaction, SimDirectory dir, ReadOnlySpan<char> fileName, OpenFileMode mode, int fileSize, bool throwsIfNotExists)
        {
            SimFileStream fs;
            if (mode > OpenFileMode.Open)
            {
                var blockCount = 1;
                if (fileSize > 0)
                    blockCount = SimUtil.Number.NextMultipleOf(fileSize, _fsMan.Head.BlockSize) / _fsMan.Head.BlockSize;
                fs = dir.GetOrCreateFile(transaction, fileName, blockCount);
            }
            else
            {
                if (!dir.TryGetFile(null, fileName, out fs))
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

        public SimDirectoryInfo GetDirectoryInfo(ReadOnlySpan<char> path, bool throwsIfDirNotExist = false)
        {
            CheckPath(ref path);
            var dir = GetDirectory(path);
            if (dir == null)
            {
                if (throwsIfDirNotExist)
                    throw new SimFSException(ExceptionType.DirectoryNotFound, path.ToString());
                return default;
            }
            return new SimDirectoryInfo(_fsMan, dir);
        }

        public ReadOnlySpan<byte> ReadFileAttributes(ReadOnlySpan<char> path, bool throwsIfDirNotExist = false)
        {
            var fi = GetFileInfo(path, throwsIfDirNotExist);
            return fi.Exists ? fi.Attributes : default;
        }


        public ReadOnlySpan<byte> ReadFileAttributes(SimFileInfo fi)
        {
            return fi.Exists ? fi.Attributes : default;
        }

        public ReadOnlyMemory<char>[] GetFiles(ReadOnlySpan<char> path, OutPathKind pathKind = OutPathKind.Relative, bool topDirectoryOnly = true, bool throwsIfDirNotExist = false)
        {
            CheckPath(ref path);
            var dir = GetDirectory(path);
            if (dir == null)
            {
                if (throwsIfDirNotExist)
                    throw new SimFSException(ExceptionType.DirectoryNotFound, path.ToString());
                return Array.Empty<ReadOnlyMemory<char>>();
            }
            return dir.GetFiles(pathKind, topDirectoryOnly);
        }

        public void GetFiles(ReadOnlySpan<char> path, ICollection<ReadOnlyMemory<char>> fileNames, OutPathKind pathKind = OutPathKind.Relative, bool topDirectoryOnly = true, bool throwsIfDirNotExist = false)
        {
            CheckPath(ref path);
            var dir = GetDirectory(path);
            if (dir == null)
            {
                if (throwsIfDirNotExist)
                    throw new SimFSException(ExceptionType.DirectoryNotFound, path.ToString());
                return;
            }
            dir.GetFiles(fileNames, pathKind, topDirectoryOnly);
        }

        public ReadOnlyMemory<char>[] GetDirectories(ReadOnlySpan<char> path, OutPathKind pathKind = OutPathKind.Relative, bool topDirectoryOnly = true, bool throwsIfDirNotExist = false)
        {
            CheckPath(ref path);
            var dir = GetDirectory(path);
            if (dir == null)
            {
                if (throwsIfDirNotExist)
                    throw new SimFSException(ExceptionType.DirectoryNotFound, path.ToString());
                return Array.Empty<ReadOnlyMemory<char>>();
            }
            return dir.GetDirectories(pathKind, topDirectoryOnly);
        }

        public void GetDirectories(ReadOnlySpan<char> path, ICollection<ReadOnlyMemory<char>> dirNames, OutPathKind pathKind = OutPathKind.Relative, bool topDirectoryOnly = true, bool throwsIfDirNotExist = false)
        {
            CheckPath(ref path);
            var dir = GetDirectory(path);
            if (dir == null)
            {
                if (throwsIfDirNotExist)
                    throw new SimFSException(ExceptionType.DirectoryNotFound, path.ToString());
                return;
            }
            dir.GetDirectories(dirNames, pathKind, topDirectoryOnly);
        }

        public void CreateDirectory(ReadOnlySpan<char> path, Transaction transaction = null, bool throwsIfAlreadyExists = false)
        {
            using var et = EnsureTransaction(transaction);
            GetDirectory(et, path, out var created);
            if (!created && throwsIfAlreadyExists)
                throw new SimFSException(ExceptionType.DirectoryAlreadyExists);
        }

        public void CreateParentDirectory(ReadOnlySpan<char> path, Transaction transaction, bool throwsIfAlreadyExists = false)
        {
            using var et = EnsureTransaction(transaction);
            GetParentDirectory(transaction, path, out _, out var dirCreated);
            if (!dirCreated && throwsIfAlreadyExists)
                throw new SimFSException(ExceptionType.DirectoryAlreadyExists);
        }

        private SimDirectory GetParentDirectory(ReadOnlySpan<char> path) =>
            GetParentDirectory(null, path, out _);
        private SimDirectory GetParentDirectory(Transaction transaction, ReadOnlySpan<char> path, bool createIfNotExist = false) =>
            GetParentDirectory(transaction, path, out _, out _, createIfNotExist);
        private SimDirectory GetParentDirectory(ReadOnlySpan<char> path, out ReadOnlySpan<char> fileName) =>
            GetParentDirectory(null, path, out fileName);
        private SimDirectory GetParentDirectory(Transaction transaction, ReadOnlySpan<char> path, out ReadOnlySpan<char> fileName, bool createIfNotExist = false) =>
            GetParentDirectory(transaction, path, out fileName, out _, createIfNotExist);
        private SimDirectory GetParentDirectory(Transaction transaction, ReadOnlySpan<char> path, out ReadOnlySpan<char> fileName, out bool dirCreated) =>
            GetParentDirectory(transaction, path, out fileName, out dirCreated, true);

        private SimDirectory GetParentDirectory(Transaction transaction, ReadOnlySpan<char> path, out ReadOnlySpan<char> fileName, out bool dirCreated, bool createIfNotExist) =>
            GetParentDirectoryRelatively(transaction, _fsMan.RootDirectory, path, out fileName, out dirCreated, createIfNotExist);

        internal static SimDirectory GetParentDirectoryRelatively(Transaction transaction, SimDirectory baseDir, ReadOnlySpan<char> path, out ReadOnlySpan<char> fileName, out bool dirCreated, bool createIfNotExist)
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
                        baseDir = baseDir.CreateDirectory(transaction, seg);
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

        private SimDirectory GetDirectory(ReadOnlySpan<char> path) =>
            GetDirectory(null, path, out _, false);
        private SimDirectory GetDirectory(Transaction transaction, ReadOnlySpan<char> path, bool createIfNotExist = false)
            => GetDirectory(transaction, path, out _, createIfNotExist);
        private SimDirectory GetDirectory(Transaction transaction, ReadOnlySpan<char> path, out bool dirCreated)
            => GetDirectory(transaction, path, out dirCreated, true);

        private SimDirectory GetDirectory(Transaction transaction, ReadOnlySpan<char> path, out bool dirCreated, bool createIfNotExist)
        {
            dirCreated = false;
            if (path.CompareTo("/", StringComparison.Ordinal) == 0)
                return _fsMan.RootDirectory;
            var parent = GetParentDirectory(transaction, path, out var dirName, out _, createIfNotExist);
            if (parent == null) return null;
            if (!parent.TryGetDirectory(dirName, out var subDir))
            {
                if (createIfNotExist)
                {
                    subDir = parent.CreateDirectory(transaction, dirName);
                    dirCreated = true;
                }
            }
            return subDir;
        }

        public void ClearDirectory(ReadOnlySpan<char> path, Transaction transaction = null, bool throwsIfNotExist = false)
        {
            using var et = EnsureTransaction(transaction);
            var dir = GetDirectory(et, path, false);
            if (dir == null)
            {
                if (throwsIfNotExist)
                    throw new SimFSException(ExceptionType.DirectoryNotFound);
            }
            else
                dir.Clear(et);
        }

        public void WriteAllText(ReadOnlySpan<char> path, ReadOnlySpan<char> text, Transaction transaction = null)
        {
            using var fs = OpenFile(path, OpenFileMode.Truncate, transaction);
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

        public void WriteAllBytes(ReadOnlySpan<char> path, ReadOnlySpan<byte> bytes, Transaction transaction = null)
        {
            using var et = EnsureTransaction(transaction);
            using var fs = OpenFile(path, OpenFileMode.Truncate, et);
            fs.Write(bytes);
        }

        public void WriteAllLines<T>(ReadOnlySpan<char> path, T lines, Transaction transaction = null) where T : IEnumerable<string>
        {
            using var et = EnsureTransaction(transaction);
            using var fs = OpenFile(path, OpenFileMode.Truncate, et);
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

        public void AppendAllText(ReadOnlySpan<char> path, ReadOnlySpan<char> text, Transaction transaction = null)
        {
            using var et = EnsureTransaction(transaction);
            using var fs = OpenFile(path, OpenFileMode.Append, et);
            WriteTextToFile(fs, text);
        }

        public void AppendAllLines<T>(ReadOnlySpan<char> path, T lines, Transaction transaction = null) where T : IEnumerable<string>
        {
            using var et = EnsureTransaction(transaction);
            using var fs = OpenFile(path, OpenFileMode.Append, et);
            using var sw = new StreamWriter(fs);
            foreach (var x in lines)
            {
                sw.WriteLine(x);
            }
        }

        public void Backup(Stream stream, Span<byte> buffer = default)
        {
            _fsMan.Backup(stream, buffer);
        }

        public void Dispose()
        {
            _fsMan.Dispose();
        }

        public void ForceDispose()
        {
            _fsMan.ForceDispose();
        }
    }
}
