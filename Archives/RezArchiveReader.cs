using System.IO;
using System.Text;

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
    private static readonly Encoding TextEncoding = Encoding.ASCII;

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

        using var source = File.OpenRead(archive.FilePath);
        using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

        source.Position = file.DataOffset;
        CopyExactly(source, destination, file.Size);
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
        if (!IsUsableName(name) ||
            string.IsNullOrWhiteSpace(extension) ||
            fileSize < 0 ||
            dataOffset < 0 ||
            dataOffset + (long)fileSize > fileLength)
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
