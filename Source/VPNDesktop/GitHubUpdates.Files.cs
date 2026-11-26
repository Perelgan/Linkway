using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace VPNDesktop;

internal static partial class GitHubUpdates
{
    private const string ManifestAssetName = "VPNDesktop-win-x64.files.json";
    private const string StagedManifestName = "update-files.json";
    private const long MaxManifest = 2L * 1024 * 1024;
    private static readonly string[] RequiredFiles =
    [
        "VPNDesktop.exe", "README.md", "Libraries/VPNDesktop.App.exe", "Libraries/VPNDesktop.dll",
        "Libraries/coreclr.dll", "Libraries/mihomo.exe", "Libraries/wintun.dll"
    ];

    private sealed record UpdateFile(string Path, long Size, string Sha256, long Offset, long CompressedSize, int Compression);
    private sealed record FileIndex(long ArchiveSize, string ArchiveSha256, UpdateFile[] Files);
    private sealed class RangeUnsupportedException : IOException;

    private static async Task PrepareChangedFilesAsync(GitHubRelease release, string installRoot, string stage,
        IProgress<int> progress, CancellationToken token)
    {
        var manifest = release.Manifest!;
        string manifestPath = Path.Combine(stage, ManifestAssetName);
        await DownloadAsync(new Uri(release.Download, ManifestAssetName), manifest.Digest, manifest.Size,
            manifestPath, null, token);
        byte[] manifestBytes = await File.ReadAllBytesAsync(manifestPath, token);
        var index = ReadFileIndex(manifestBytes, release);
        string payload = Path.Combine(stage, "package", "VPNDesktop");
        Directory.CreateDirectory(payload);

        var changed = new List<UpdateFile>();
        foreach (var file in index.Files)
        {
            token.ThrowIfCancellationRequested();
            if (!InstalledFileMatches(file, installRoot, token))
                changed.Add(file);
        }

        long totalBytes = changed.Sum(file => file.CompressedSize);
        long downloaded = 0;
        Uri url = release.Download;
        foreach (var file in changed)
        {
            string destination = Path.Combine(payload, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (file.CompressedSize == 0)
            {
                await WriteVerifiedFileAsync(Stream.Null, destination, file, token);
                continue;
            }

            var range = await OpenRangeAsync(url, file, release.Size, token);
            using var response = range.Response;
            url = range.Url;
            await using var raw = await response.Content.ReadAsStreamAsync(token);
            await using Stream content = file.Compression == 8 ? new DeflateStream(raw, CompressionMode.Decompress, leaveOpen: true) : raw;
            await WriteVerifiedFileAsync(content, destination, file, token);
            downloaded += file.CompressedSize;
            progress.Report((int)(downloaded * 100 / totalBytes));
        }

        string dll = changed.Any(file => file.Path.Equals("Libraries/VPNDesktop.dll", StringComparison.OrdinalIgnoreCase))
            ? Path.Combine(payload, "Libraries", "VPNDesktop.dll")
            : Path.Combine(installRoot, "Libraries", "VPNDesktop.dll");
        if (ParseProductVersion(System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).ProductVersion) != release.Version)
            throw new IOException("Версия файлов обновления отличается от тега GitHub.");
        await File.WriteAllBytesAsync(Path.Combine(stage, StagedManifestName), manifestBytes, token);
        File.Delete(manifestPath);
        progress.Report(100);
    }

    private static FileIndex ReadFileIndex(byte[] bytes, GitHubRelease release)
    {
        FileIndex? index;
        try { index = JsonSerializer.Deserialize<FileIndex>(bytes); }
        catch (JsonException) { throw new IOException("Список файлов обновления повреждён."); }
        if (index is not { Files.Length: > 0 and <= 4096 } || index.Files.Any(file => file is null) ||
            index.ArchiveSize != release.Size ||
            !string.Equals(index.ArchiveSha256, Convert.ToHexString(release.Digest), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Список файлов не соответствует архиву релиза.");

        long end = 0, expanded = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in index.Files.OrderBy(file => file.Offset))
        {
            string[] parts = file.Path?.Split('/') ?? [];
            if (parts.Length == 0 || parts.Any(InvalidPathPart) || !paths.Add(file.Path!) ||
                (parts.Length == 1 && parts[0] is not ("README.md" or "VPNDesktop.exe")) ||
                (parts.Length > 1 && parts[0] is not ("Libraries" or "Languages" or "Licenses")) ||
                file.Sha256 is not { Length: 64 } || !file.Sha256.All(char.IsAsciiHexDigit) ||
                file.Size < 0 || file.Size > MaxExpanded - expanded || file.CompressedSize < 0 ||
                file.Offset < end || file.Offset > release.Size - file.CompressedSize ||
                file.Compression is not (0 or 8) || file.Compression == 0 && file.CompressedSize != file.Size ||
                file.CompressedSize == 0 && file.Size != 0)
                throw new IOException("В списке файлов обновления есть некорректная запись.");
            end = file.Offset + file.CompressedSize;
            expanded += file.Size;
        }
        if (!RequiredFiles.All(paths.Contains))
            throw new IOException("Список файлов обновления неполный.");
        return index;
    }

    private static bool InstalledFileMatches(UpdateFile file, string installRoot, CancellationToken token)
    {
        string[] parts = file.Path.Split('/');
        string path = installRoot;
        for (int index = 0; index < parts.Length; index++)
        {
            path = Path.Combine(path, parts[index]);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
            if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
                attributes.HasFlag(FileAttributes.Directory) != (index < parts.Length - 1))
                throw new IOException("Файлы установки содержат ссылки или несовместимую структуру каталогов.");
        }
        using var installed = File.OpenRead(path);
        if (installed.Length != file.Size)
            return false;
        token.ThrowIfCancellationRequested();
        return Convert.ToHexString(SHA256.HashData(installed)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(HttpResponseMessage Response, Uri Url)> OpenRangeAsync(Uri url,
        UpdateFile file, long archiveSize, CancellationToken token)
    {
        long end = file.Offset + file.CompressedSize - 1;
        for (int redirect = 0; redirect < 5; redirect++)
        {
            using var request = MakeRequest(url);
            request.Headers.Range = new RangeHeaderValue(file.Offset, end);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
            {
                response.Dispose();
                url = new Uri(url, next);
                if (!IsHttpsHost(url, "release-assets.githubusercontent.com") && !IsHttpsHost(url, "objects.githubusercontent.com"))
                    throw new IOException("Файл обновления перенаправлен на неизвестный сервер.");
                continue;
            }
            if (response.StatusCode == HttpStatusCode.OK)
            {
                response.Dispose();
                throw new RangeUnsupportedException();
            }
            var range = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" ||
                range.From != file.Offset || range.To != end || range.Length != archiveSize ||
                response.Content.Headers.ContentLength != file.CompressedSize)
            {
                response.Dispose();
                throw new IOException("GitHub вернул некорректный диапазон файла обновления.");
            }
            return (response, url);
        }
        throw new IOException("Слишком много перенаправлений при скачивании файлов обновления.");
    }

    private static async Task WriteVerifiedFileAsync(Stream content, string destination, UpdateFile file, CancellationToken token)
    {
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await content.ReadAsync(buffer, token)) != 0)
        {
            total += count;
            if (total > file.Size)
                throw new IOException("Распакованный файл обновления больше заявленного размера.");
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        if (total != file.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Контрольная сумма файла обновления не совпадает.");
    }
}
