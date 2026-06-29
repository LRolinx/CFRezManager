using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CFRezManager;

internal static class RezArchiveDirectoryCache
{
    private const int CacheVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private static readonly string CacheDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "RezIndexCache",
        "v1");

    public static bool TryLoad(string filePath, out RezArchive? archive)
    {
        archive = null;

        try
        {
            var sourceInfo = new FileInfo(filePath);
            if (!sourceInfo.Exists)
            {
                return false;
            }

            string cachePath = GetCachePath(sourceInfo.FullName);
            if (!File.Exists(cachePath))
            {
                return false;
            }

            byte[] json = File.ReadAllBytes(cachePath);
            RezArchiveCacheFile? cacheFile = JsonSerializer.Deserialize<RezArchiveCacheFile>(json, JsonOptions);
            if (cacheFile is null ||
                cacheFile.Version != CacheVersion ||
                !string.Equals(cacheFile.SourcePath, sourceInfo.FullName, StringComparison.OrdinalIgnoreCase) ||
                cacheFile.SourceLength != sourceInfo.Length ||
                cacheFile.SourceLastWriteTimeUtcTicks != sourceInfo.LastWriteTimeUtc.Ticks ||
                cacheFile.Header is null ||
                cacheFile.Root is null)
            {
                return false;
            }

            RezDirectoryNode root = RestoreDirectory(cacheFile.Root);
            archive = new RezArchive(sourceInfo.FullName, RestoreHeader(cacheFile.Header), root)
            {
                DirectoryCount = cacheFile.DirectoryCount,
                FileCount = cacheFile.FileCount
            };
            return true;
        }
        catch
        {
            archive = null;
            return false;
        }
    }

    public static void TrySave(RezArchive archive)
    {
        try
        {
            var sourceInfo = new FileInfo(archive.FilePath);
            if (!sourceInfo.Exists)
            {
                return;
            }

            Directory.CreateDirectory(CacheDirectory);
            string cachePath = GetCachePath(sourceInfo.FullName);
            string tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var cacheFile = new RezArchiveCacheFile
            {
                Version = CacheVersion,
                SourcePath = sourceInfo.FullName,
                SourceLength = sourceInfo.Length,
                SourceLastWriteTimeUtcTicks = sourceInfo.LastWriteTimeUtc.Ticks,
                Header = StoreHeader(archive.Header),
                Root = StoreDirectory(archive.Root),
                DirectoryCount = archive.DirectoryCount,
                FileCount = archive.FileCount
            };

            byte[] json = JsonSerializer.SerializeToUtf8Bytes(cacheFile, JsonOptions);
            File.WriteAllBytes(tempPath, json);
            File.Move(tempPath, cachePath, overwrite: true);
        }
        catch
        {
        }
    }

    private static string GetCachePath(string sourcePath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath.ToUpperInvariant()));
        string key = Convert.ToHexString(hash).ToLowerInvariant();
        return Path.Combine(CacheDirectory, key + ".json");
    }

    private static RezHeader RestoreHeader(RezHeaderCacheModel header)
    {
        return new RezHeader(
            header.FileType,
            header.UserTitle,
            header.Version,
            header.RootDirPos,
            header.RootDirSize,
            header.RootDirTime,
            header.NextWritePos,
            header.Time,
            header.LargestKeyAry,
            header.LargestDirNameSize,
            header.LargestRezNameSize,
            header.LargestCommentSize,
            header.IsSorted);
    }

    private static RezHeaderCacheModel StoreHeader(RezHeader header)
    {
        return new RezHeaderCacheModel
        {
            FileType = header.FileType,
            UserTitle = header.UserTitle,
            Version = header.Version,
            RootDirPos = header.RootDirPos,
            RootDirSize = header.RootDirSize,
            RootDirTime = header.RootDirTime,
            NextWritePos = header.NextWritePos,
            Time = header.Time,
            LargestKeyAry = header.LargestKeyAry,
            LargestDirNameSize = header.LargestDirNameSize,
            LargestRezNameSize = header.LargestRezNameSize,
            LargestCommentSize = header.LargestCommentSize,
            IsSorted = header.IsSorted
        };
    }

    private static RezDirectoryNode RestoreDirectory(RezNodeCacheModel node)
    {
        var directory = new RezDirectoryNode(
            node.Name ?? string.Empty,
            node.FullPath ?? string.Empty,
            node.TableOffset,
            node.TableSize);

        foreach (RezNodeCacheModel child in node.Children ?? [])
        {
            if (child.IsDirectory)
            {
                directory.Children.Add(RestoreDirectory(child));
            }
            else
            {
                directory.Children.Add(new RezFileNode(
                    child.Name ?? string.Empty,
                    child.FullPath ?? string.Empty,
                    child.Extension ?? string.Empty,
                    child.DataOffset,
                    child.Size,
                    child.Time,
                    child.Id,
                    child.Md5 ?? string.Empty));
            }
        }

        return directory;
    }

    private static RezNodeCacheModel StoreDirectory(RezDirectoryNode directory)
    {
        return new RezNodeCacheModel
        {
            IsDirectory = true,
            Name = directory.Name,
            FullPath = directory.FullPath,
            TableOffset = directory.TableOffset,
            TableSize = directory.TableSize,
            Children = directory.Children.Select(StoreNode).ToList()
        };
    }

    private static RezNodeCacheModel StoreNode(RezNode node)
    {
        if (node is RezDirectoryNode directory)
        {
            return StoreDirectory(directory);
        }

        var file = (RezFileNode)node;
        return new RezNodeCacheModel
        {
            IsDirectory = false,
            Name = file.Name,
            FullPath = file.FullPath,
            Extension = file.Extension,
            DataOffset = file.DataOffset,
            Size = file.Size,
            Time = file.Time,
            Id = file.Id,
            Md5 = file.Md5
        };
    }

    private sealed class RezArchiveCacheFile
    {
        public int Version { get; set; }
        public string SourcePath { get; set; } = string.Empty;
        public long SourceLength { get; set; }
        public long SourceLastWriteTimeUtcTicks { get; set; }
        public RezHeaderCacheModel? Header { get; set; }
        public RezNodeCacheModel? Root { get; set; }
        public int DirectoryCount { get; set; }
        public int FileCount { get; set; }
    }

    private sealed class RezHeaderCacheModel
    {
        public string FileType { get; set; } = string.Empty;
        public string UserTitle { get; set; } = string.Empty;
        public int Version { get; set; }
        public int RootDirPos { get; set; }
        public int RootDirSize { get; set; }
        public int RootDirTime { get; set; }
        public int NextWritePos { get; set; }
        public int Time { get; set; }
        public int LargestKeyAry { get; set; }
        public int LargestDirNameSize { get; set; }
        public int LargestRezNameSize { get; set; }
        public int LargestCommentSize { get; set; }
        public byte IsSorted { get; set; }
    }

    private sealed class RezNodeCacheModel
    {
        public bool IsDirectory { get; set; }
        public string? Name { get; set; }
        public string? FullPath { get; set; }
        public int TableOffset { get; set; }
        public int TableSize { get; set; }
        public string? Extension { get; set; }
        public int DataOffset { get; set; }
        public int Size { get; set; }
        public int Time { get; set; }
        public int Id { get; set; }
        public string? Md5 { get; set; }
        public List<RezNodeCacheModel>? Children { get; set; }
    }
}
