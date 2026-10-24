using System.IO;
using System.Text;
using System.Collections.Concurrent;

namespace CFRezManager;

public sealed class RezArchive
{
    public RezArchive(string filePath, RezHeader header, RezDirectoryNode root)
    {
        FilePath = filePath;
        Header = header;
        Root = root;
    }

    public string FilePath { get; }
    public RezHeader Header { get; }
    public RezDirectoryNode Root { get; }
    public int DirectoryCount { get; internal set; }
    public int FileCount { get; internal set; }
}

public sealed record RezHeader(
    string FileType,
    string UserTitle,
    int Version,
    int RootDirPos,
    int RootDirSize,
    int RootDirTime,
    int NextWritePos,
    int Time,
    int LargestKeyAry,
    int LargestDirNameSize,
    int LargestRezNameSize,
    int LargestCommentSize,
    byte IsSorted);

public abstract class RezNode
{
    protected RezNode(string name, string fullPath)
    {
        Name = name;
        FullPath = fullPath;
    }

    public string Name { get; }
    public string FullPath { get; }
}

public sealed class RezDirectoryNode : RezNode
{
    public RezDirectoryNode(string name, string fullPath, int tableOffset, int tableSize)
        : base(name, fullPath)
    {
        TableOffset = tableOffset;
        TableSize = tableSize;
    }

    public int TableOffset { get; }
    public int TableSize { get; }
    public List<RezNode> Children { get; } = new();
}

public sealed class RezFileNode : RezNode
{
    public RezFileNode(
        string name,
        string fullPath,
        string extension,
        int dataOffset,
        int size,
        int time,
        int id,
        string md5)
        : base(name, fullPath)
    {
        Extension = extension;
        DataOffset = dataOffset;
        Size = size;
        Time = time;
        Id = id;
        Md5 = md5;
    }

    public string Extension { get; }
    public int DataOffset { get; }
    public int Size { get; }
    public int Time { get; }
    public int Id { get; }
    public string Md5 { get; }
}

public sealed class RezArchiveReader
{
    private const int HeaderSize = 168;
    private const int MaxDepth = 128;
    private const int MaxSplitIndex = 255;
    private static readonly Encoding TextEncoding = Encoding.ASCII;
    private static readonly ConcurrentDictionary<string, bool> RezHeaderCache = new(StringComparer.OrdinalIgnoreCase);

    public RezArchive Read(string filePath)
    {
        if (RezArchiveDirectoryCache.TryLoad(filePath, out RezArchive? cachedArchive) &&
            cachedArchive is not null)
        {
            return cachedArchive;
        }

        using var fileStream = File.OpenRead(filePath);
        using var reader = new BinaryReader(fileStream, TextEncoding, leaveOpen: false);

        var header = ReadHeader(reader);
        var root = new RezDirectoryNode(Path.GetFileName(filePath), "", header.RootDirPos, header.RootDirSize);
        var archive = new RezArchive(filePath, header, root);

        ParseEntryRange(reader, archive, root, header.RootDirPos, header.RootDirSize, 0, new HashSet<string>());
        if (root.Children.Count == 0 && header.RootDirSize > 0)
        {
            if (!TryParseLegacyEntryRange(reader, archive, root, header.RootDirPos, header.RootDirSize, 0, new HashSet<string>()))
            {
                root.Children.Clear();
                archive.DirectoryCount = 0;
                archive.FileCount = 0;
            }
        }

        RezArchiveDirectoryCache.TrySave(archive);
        return archive;
    }

    public static void ExtractFile(RezArchive archive, RezFileNode file, string destinationPath)
    {
        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        using var source = OpenFileRead(archive, file);
        using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

        CopyExactly(source, destination, file.Size);
    }

    /// <summary>
    /// Opens a resource stream whose length and readable range are limited to the
    /// indexed resource, even when the underlying bytes live in a shared REZ shard.
    /// </summary>
    public static Stream OpenFileRead(RezArchive archive, RezFileNode file)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(file);

        string dataPath = ResolveDataFilePath(archive.FilePath, file.Time);
        var source = File.OpenRead(dataPath);
        if (file.DataOffset < 0 ||
            file.Size < 0 ||
            (long)file.DataOffset + file.Size > source.Length)
        {
            source.Dispose();
            throw new InvalidDataException(
                $"Resource range is outside its data file: {file.FullPath} ({dataPath})");
        }

        source.Position = file.DataOffset;
        return new BoundedReadStream(source, file.Size);
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _source;
        private readonly long _startOffset;
        private readonly long _length;
        private long _position;

        public BoundedReadStream(Stream source, long length)
        {
            _source = source;
            _startOffset = source.Position;
            _length = length;
        }

        public override bool CanRead => _source.CanRead;
        public override bool CanSeek => _source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > buffer.Length - count)
            {
                throw new ArgumentException("The buffer range is invalid.");
            }

            int boundedCount = (int)Math.Min(count, _length - _position);
            if (boundedCount <= 0)
            {
                return 0;
            }

            int read = _source.Read(buffer, offset, boundedCount);
            _position += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int boundedCount = (int)Math.Min(buffer.Length, _length - _position);
            if (boundedCount <= 0)
            {
                return 0;
            }

            int read = _source.Read(buffer[..boundedCount]);
            _position += read;
            return read;
        }

        public override int ReadByte()
        {
            if (_position >= _length)
            {
                return -1;
            }

            int value = _source.ReadByte();
            if (value >= 0)
            {
                _position++;
            }

            return value;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (target < 0 || target > _length)
            {
                throw new IOException("Seek is outside the indexed resource.");
            }

            _source.Seek(checked(_startOffset + target), SeekOrigin.Begin);
            _position = target;
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _source.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private static string ResolveDataFilePath(string archivePath, int splitIndex)
    {
        if (splitIndex <= 0 || splitIndex > MaxSplitIndex)
        {
            return archivePath;
        }

        string? directory = Path.GetDirectoryName(archivePath);
        if (string.IsNullOrEmpty(directory))
        {
            return archivePath;
        }

        string archiveName = Path.GetFileNameWithoutExtension(archivePath);
        string extension = Path.GetExtension(archivePath);
        string candidate = Path.Combine(directory, $"{archiveName}_{splitIndex}{extension}");
        if (!File.Exists(candidate) || IsRezArchive(candidate))
        {
            return archivePath;
        }

        return candidate;
    }

    internal static bool IsRezArchive(string path)
    {
        return RezHeaderCache.GetOrAdd(path, static candidate =>
        {
            try
            {
                using var source = File.OpenRead(candidate);
                if (source.Length < HeaderSize || source.ReadByte() != 0x0D || source.ReadByte() != 0x0A)
                {
                    return false;
                }

                using var reader = new BinaryReader(source, TextEncoding, leaveOpen: true);
                string fileType = ReadFixedString(reader, 60);
                source.Position = 127;
                int version = reader.ReadInt32();
                return fileType.StartsWith("RezMgr Version", StringComparison.OrdinalIgnoreCase) && version == 1;
            }
            catch
            {
                return true;
            }
        });
    }

    private static RezHeader ReadHeader(BinaryReader reader)
    {
        reader.BaseStream.Position = 2;
        string fileType = ReadFixedString(reader, 60).TrimEnd();

        reader.BaseStream.Position = 64;
        string userTitle = ReadFixedString(reader, 60).TrimEnd();

        reader.BaseStream.Position = 127;
        return new RezHeader(
            FileType: fileType,
            UserTitle: userTitle,
            Version: reader.ReadInt32(),
            RootDirPos: reader.ReadInt32(),
            RootDirSize: reader.ReadInt32(),
            RootDirTime: reader.ReadInt32(),
            NextWritePos: reader.ReadInt32(),
            Time: reader.ReadInt32(),
            LargestKeyAry: reader.ReadInt32(),
            LargestDirNameSize: reader.ReadInt32(),
            LargestRezNameSize: reader.ReadInt32(),
            LargestCommentSize: reader.ReadInt32(),
            IsSorted: reader.ReadByte());
    }

    private static void ParseEntryRange(
        BinaryReader fileReader,
        RezArchive archive,
        RezDirectoryNode owner,
        int offset,
        int size,
        int depth,
        HashSet<string> visitedRanges)
    {
        if (depth > MaxDepth || size <= 0 || offset < HeaderSize)
        {
            return;
        }

        long fileLength = fileReader.BaseStream.Length;
        if (offset >= fileLength)
        {
            return;
        }

        int readableSize = (int)Math.Min(size, fileLength - offset);
        if (readableSize <= 0)
        {
            return;
        }

        string rangeKey = $"{offset}:{readableSize}";
        if (!visitedRanges.Add(rangeKey))
        {
            return;
        }

        using var rangeReader = CreateDecodedReader(fileReader, offset, readableSize);
        while (rangeReader.BaseStream.Position + sizeof(int) <= rangeReader.BaseStream.Length)
        {
            long entryStart = rangeReader.BaseStream.Position;
            int type = rangeReader.ReadInt32();

            if (type == 0)
            {
                if (!TryReadFileEntry(rangeReader, archive, owner, fileLength))
                {
                    break;
                }
            }
            else if (type == 1)
            {
                if (!TryReadDirectoryEntry(rangeReader, fileReader, archive, owner, depth, visitedRanges))
                {
                    break;
                }
            }
            else
            {
                break;
            }

            if (rangeReader.BaseStream.Position <= entryStart)
            {
                break;
            }
        }
    }

    private static bool TryReadDirectoryEntry(
        BinaryReader rangeReader,
        BinaryReader fileReader,
        RezArchive archive,
        RezDirectoryNode owner,
        int depth,
        HashSet<string> visitedRanges)
    {
        if (RemainingBytes(rangeReader) < 16)
        {
            return false;
        }

        int tableOffset = rangeReader.ReadInt32();
        int tableSize = rangeReader.ReadInt32();
        _ = rangeReader.ReadInt32();
        int nameLength = rangeReader.ReadInt32();

        if (nameLength < 0 || RemainingBytes(rangeReader) < nameLength + 1)
        {
            return false;
        }

        string name = ReadFixedString(rangeReader, nameLength);
        rangeReader.ReadByte();

        if (!IsUsableName(name) || !IsUsableRange(fileReader.BaseStream.Length, tableOffset, tableSize))
        {
            return true;
        }

        string fullPath = CombineRezPath(owner.FullPath, name);
        var directory = new RezDirectoryNode(name, fullPath, tableOffset, tableSize);
        owner.Children.Add(directory);
        archive.DirectoryCount++;

        ParseEntryRange(fileReader, archive, directory, tableOffset, tableSize, depth + 1, visitedRanges);
        return true;
    }

    private static bool TryReadFileEntry(
        BinaryReader rangeReader,
        RezArchive archive,
        RezDirectoryNode owner,
        long fileLength)
    {
        if (RemainingBytes(rangeReader) < 28)
        {
            return false;
        }

        int dataOffset = rangeReader.ReadInt32();
        int fileSize = rangeReader.ReadInt32();
        int time = rangeReader.ReadInt32();
        int id = rangeReader.ReadInt32();
        byte[] extensionBytes = rangeReader.ReadBytes(4);
        _ = rangeReader.ReadInt32();
        int nameLength = rangeReader.ReadInt32();

        if (nameLength < 0 || RemainingBytes(rangeReader) < nameLength + 34)
        {
            return false;
        }

        string name = ReadFixedString(rangeReader, nameLength);
        rangeReader.ReadBytes(2);
        string md5 = ReadFixedString(rangeReader, 32);

        string extension = DecodeExtension(extensionBytes);
        string dataPath = ResolveDataFilePath(archive.FilePath, time);
        long dataLength = string.Equals(dataPath, archive.FilePath, StringComparison.OrdinalIgnoreCase)
            ? fileLength
            : new FileInfo(dataPath).Length;
        if (!IsUsableName(name) ||
            string.IsNullOrWhiteSpace(extension) ||
            fileSize < 0 ||
            dataOffset < 0 ||
            dataOffset + (long)fileSize > dataLength)
        {
            return true;
        }

        string fileName = $"{name}.{extension}";
        string fullPath = CombineRezPath(owner.FullPath, fileName);
        owner.Children.Add(new RezFileNode(fileName, fullPath, extension, dataOffset, fileSize, time, id, md5));
        archive.FileCount++;
        return true;
    }

    private static BinaryReader CreateDecodedReader(BinaryReader fileReader, int offset, int size)
    {
        fileReader.BaseStream.Position = offset;
        byte[] buffer = fileReader.ReadBytes(size);
        RezCrypto.Decode(buffer, offset);
        return new BinaryReader(new MemoryStream(buffer), TextEncoding, leaveOpen: false);
    }

    private static bool TryParseLegacyEntryRange(
        BinaryReader fileReader,
        RezArchive archive,
        RezDirectoryNode owner,
        int offset,
        int size,
        int depth,
        HashSet<string> visitedRanges)
    {
        if (depth > MaxDepth || size <= 0 || !IsUsableRange(fileReader.BaseStream.Length, offset, size))
        {
            return false;
        }

        string rangeKey = $"legacy:{offset}:{size}";
        if (!visitedRanges.Add(rangeKey))
        {
            return true;
        }

        long originalPosition = fileReader.BaseStream.Position;
        byte[] bytes;
        try
        {
            fileReader.BaseStream.Position = offset;
            bytes = fileReader.ReadBytes(size);
        }
        finally
        {
            fileReader.BaseStream.Position = originalPosition;
        }

        using var rangeReader = new BinaryReader(new MemoryStream(bytes), TextEncoding, leaveOpen: false);
        var directories = new List<(RezDirectoryNode Node, int Offset, int Size)>();
        var files = new List<RezFileNode>();

        while (rangeReader.BaseStream.Position < rangeReader.BaseStream.Length)
        {
            long entryStart = rangeReader.BaseStream.Position;
            long remaining = RemainingBytes(rangeReader);
            if (remaining < sizeof(int))
            {
                if (bytes.AsSpan((int)entryStart).IndexOfAnyExcept((byte)0) < 0)
                {
                    break;
                }

                return false;
            }

            int type = rangeReader.ReadInt32();
            if (type is not (0 or 1))
            {
                return false;
            }

            if (type == 1)
            {
                if (RemainingBytes(rangeReader) < 12)
                {
                    return false;
                }

                int tableOffset = rangeReader.ReadInt32();
                int tableSize = rangeReader.ReadInt32();
                _ = rangeReader.ReadInt32();
                if (!TryReadLegacyName(rangeReader, out string name) ||
                    !IsUsableName(name) ||
                    !IsUsableRange(fileReader.BaseStream.Length, tableOffset, tableSize))
                {
                    return false;
                }

                string fullPath = CombineRezPath(owner.FullPath, name);
                directories.Add((new RezDirectoryNode(name, fullPath, tableOffset, tableSize), tableOffset, tableSize));
            }
            else
            {
                if (RemainingBytes(rangeReader) < 24)
                {
                    return false;
                }

                int dataOffset = rangeReader.ReadInt32();
                int fileSize = rangeReader.ReadInt32();
                int time = rangeReader.ReadInt32();
                int id = rangeReader.ReadInt32();
                byte[] extensionBytes = rangeReader.ReadBytes(4);
                _ = rangeReader.ReadInt32();
                if (!TryReadLegacyName(rangeReader, out string name) || !IsUsableName(name))
                {
                    return false;
                }

                string extension = DecodeExtension(extensionBytes);
                string dataPath = ResolveDataFilePath(archive.FilePath, time);
                long dataLength;
                try
                {
                    dataLength = string.Equals(dataPath, archive.FilePath, StringComparison.OrdinalIgnoreCase)
                        ? fileReader.BaseStream.Length
                        : new FileInfo(dataPath).Length;
                }
                catch
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(extension) ||
                    fileSize < 0 ||
                    dataOffset < 0 ||
                    dataOffset + (long)fileSize > dataLength)
                {
                    return false;
                }

                string fileName = $"{name}.{extension}";
                files.Add(new RezFileNode(fileName, CombineRezPath(owner.FullPath, fileName), extension, dataOffset, fileSize, time, id, string.Empty));
            }

            if (!TryAdvanceLegacyEntry(rangeReader, archive, fileReader.BaseStream.Length))
            {
                return false;
            }

            if (rangeReader.BaseStream.Position <= entryStart)
            {
                return false;
            }
        }

        if (directories.Count == 0 && files.Count == 0)
        {
            return false;
        }

        owner.Children.AddRange(directories.Select(directory => directory.Node));
        owner.Children.AddRange(files);
        archive.DirectoryCount += directories.Count;
        archive.FileCount += files.Count;

        foreach ((RezDirectoryNode directory, int tableOffset, int tableSize) in directories)
        {
            if (tableSize > 0 && !TryParseLegacyEntryRange(fileReader, archive, directory, tableOffset, tableSize, depth + 1, visitedRanges))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadLegacyName(BinaryReader reader, out string name)
    {
        name = string.Empty;
        long remaining = RemainingBytes(reader);
        if (remaining <= 0 || remaining > int.MaxValue)
        {
            return false;
        }

        byte[] bytes = reader.ReadBytes((int)Math.Min(remaining, 1024));
        int terminator = Array.IndexOf(bytes, (byte)0);
        if (terminator <= 0)
        {
            return false;
        }

        name = TextEncoding.GetString(bytes, 0, terminator);
        reader.BaseStream.Position = reader.BaseStream.Position - bytes.Length + terminator + 1;
        return name.All(character => character is >= ' ' and <= '~');
    }

    private static bool TryAdvanceLegacyEntry(BinaryReader reader, RezArchive archive, long fileLength)
    {
        long position = reader.BaseStream.Position;
        long length = reader.BaseStream.Length;
        if (position == length)
        {
            return true;
        }

        for (int padding = 0; padding <= 3 && position + padding + sizeof(int) <= length; padding++)
        {
            if (padding > 0)
            {
                reader.BaseStream.Position = position + padding - 1;
                if (reader.ReadByte() != 0)
                {
                    break;
                }
            }

            if (IsPlausibleLegacyEntryAt(reader, position + padding, archive, fileLength))
            {
                reader.BaseStream.Position = position + padding;
                return true;
            }
        }

        reader.BaseStream.Position = position;
        byte[] trailing = reader.ReadBytes((int)RemainingBytes(reader));
        if (trailing.All(value => value == 0))
        {
            reader.BaseStream.Position = length;
            return true;
        }

        return false;
    }

    private static bool IsPlausibleLegacyEntryAt(BinaryReader reader, long position, RezArchive archive, long fileLength)
    {
        long originalPosition = reader.BaseStream.Position;
        try
        {
            reader.BaseStream.Position = position;
            if (RemainingBytes(reader) < sizeof(int))
            {
                return false;
            }

            int type = reader.ReadInt32();
            if (type == 1)
            {
                if (RemainingBytes(reader) < 12)
                {
                    return false;
                }

                int tableOffset = reader.ReadInt32();
                int tableSize = reader.ReadInt32();
                _ = reader.ReadInt32();
                return TryReadLegacyName(reader, out string name) &&
                       IsUsableName(name) &&
                       IsUsableRange(fileLength, tableOffset, tableSize);
            }

            if (type != 0 || RemainingBytes(reader) < 24)
            {
                return false;
            }

            int dataOffset = reader.ReadInt32();
            int size = reader.ReadInt32();
            int time = reader.ReadInt32();
            _ = reader.ReadInt32();
            byte[] extensionBytes = reader.ReadBytes(4);
            _ = reader.ReadInt32();
            if (!TryReadLegacyName(reader, out string fileName) || !IsUsableName(fileName))
            {
                return false;
            }

            string extension = DecodeExtension(extensionBytes);
            string dataPath = ResolveDataFilePath(archive.FilePath, time);
            long dataLength = string.Equals(dataPath, archive.FilePath, StringComparison.OrdinalIgnoreCase)
                ? fileLength
                : new FileInfo(dataPath).Length;
            return !string.IsNullOrWhiteSpace(extension) &&
                   size >= 0 &&
                   dataOffset >= 0 &&
                   dataOffset + (long)size <= dataLength;
        }
        catch
        {
            return false;
        }
        finally
        {
            reader.BaseStream.Position = originalPosition;
        }
    }

    private static string DecodeExtension(byte[] extensionBytes)
    {
        string raw = TextEncoding.GetString(extensionBytes).TrimEnd('\0', ' ');
        return new string(raw.Reverse().ToArray());
    }

    private static bool IsUsableRange(long fileLength, int offset, int size)
    {
        return offset >= HeaderSize && size >= 0 && offset + (long)size <= fileLength;
    }

    private static bool IsUsableName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.All(c => !char.IsControl(c) && c != Path.DirectorySeparatorChar && c != Path.AltDirectorySeparatorChar);
    }

    private static long RemainingBytes(BinaryReader reader)
    {
        return reader.BaseStream.Length - reader.BaseStream.Position;
    }

    private static string ReadFixedString(BinaryReader reader, int length)
    {
        byte[] bytes = reader.ReadBytes(length);
        return TextEncoding.GetString(bytes).TrimEnd('\0');
    }

    private static string CombineRezPath(string parent, string name)
    {
        return string.IsNullOrEmpty(parent) ? name : $"{parent}/{name}";
    }

    private static void CopyExactly(Stream source, Stream destination, int bytesToCopy)
    {
        byte[] buffer = new byte[1024 * 128];
        int remaining = bytesToCopy;

        while (remaining > 0)
        {
            int read = source.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of REZ file while extracting.");
            }

            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }
}
