using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace VPNDesktop;

internal sealed record MihomoRelease(Version Version, string AssetName, byte[] Digest, long Size)
{
    public Uri Address => new($"https://github.com/MetaCubeX/mihomo/releases/download/v{Version.ToString(3)}/{AssetName}");
}

internal sealed record MihomoInstallation(Version Version, string Executable, bool Bundled);

internal static class MihomoVersions
{
    internal static readonly Version BundledVersion = new(1, 19, 34);
    private static readonly Version MinimumVersion = new(1, 19, 33);
    private const long MaxArchive = 96L * 1024 * 1024;
    private const long MaxExecutable = 160L * 1024 * 1024;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Cores"));
    private static string SelectionPath => Path.Combine(Root, "selected.json");
    private sealed record KernelSelection(string Tag, string ArchiveHash);
    private static KernelSelection? selection = ReadSelection();

    public static MihomoInstallation Active
    {
        get
        {
            if (selection is { } saved && InstalledDirectory(saved) is { } path)
                return new(Version.Parse(saved.Tag.AsSpan(1)), Path.Combine(path, "mihomo.exe"), false);
            return new(BundledVersion, Path.Combine(AppContext.BaseDirectory, "mihomo.exe"), true);
        }
    }

    public static async Task<MihomoRelease[]> ListAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/MetaCubeX/mihomo/releases?per_page=12");
        request.Headers.UserAgent.ParseAdd($"Linkway/{GitHubUpdates.InstalledVersion}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ReadReleases(json.RootElement);
    }

    internal static MihomoRelease[] ReadReleases(JsonElement releases)
    {
        var result = new List<MihomoRelease>();
        foreach (var release in releases.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean() ||
                !TryVersion(release.GetProperty("tag_name").GetString(), out var version))
                continue;

            string tag = $"v{version.ToString(3)}";
            foreach (string variant in new[] { "compatible", "v1" })
            {
                string name = $"mihomo-windows-amd64-{variant}-{tag}.zip";
                var asset = release.GetProperty("assets").EnumerateArray().FirstOrDefault(item =>
                    item.GetProperty("name").GetString() == name &&
                    item.GetProperty("state").GetString() == "uploaded" &&
                    item.GetProperty("browser_download_url").GetString() ==
                        $"https://github.com/MetaCubeX/mihomo/releases/download/{tag}/{name}");
                if (asset.ValueKind == JsonValueKind.Undefined ||
                    !asset.TryGetProperty("digest", out var hash) || hash.GetString() is not { Length: 71 } digest ||
                    !digest.StartsWith("sha256:", StringComparison.Ordinal) || !digest[7..].All(char.IsAsciiHexDigit) ||
                    !asset.TryGetProperty("size", out var bytes) || !bytes.TryGetInt64(out long size) ||
                    size is <= 0 or > MaxArchive)
                    continue;

                result.Add(new(version, name, Convert.FromHexString(digest[7..]), size));
                break;
            }
        }
        return result.OrderByDescending(item => item.Version).DistinctBy(item => item.Version).ToArray();
    }

    public static async Task SelectAsync(MihomoRelease? release, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (release is null || release.Version == BundledVersion)
        {
            if (PlainDirectory(Root))
                File.Delete(SelectionPath);
            selection = null;
            return;
        }

        string tag = $"v{release.Version.ToString(3)}";
        string hash = Convert.ToHexString(release.Digest);
        var target = new KernelSelection(tag, hash);
        Directory.CreateDirectory(Root);
        if (!PlainDirectory(Root))
            throw new IOException("Папка версий Mihomo недоступна.");

        string? existing = InstalledDirectory(target);
        if (existing is not null && CheckCachedFiles(existing))
        {
            await Mihomo.CheckExecutableAsync(Path.Combine(existing, "mihomo.exe"), release.Version, cancellationToken);
            SaveSelection(target, cancellationToken);
            return;
        }

        string temporary = Path.Combine(Root, ".mihomo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string archive = Path.Combine(temporary, "download.zip");
            await GitHubUpdates.DownloadAsync(release.Address, release.Digest, release.Size, archive, null, cancellationToken);
            string executable = Path.Combine(temporary, "mihomo.exe");
            await ExtractExecutableAsync(archive, release.AssetName, executable, cancellationToken);
            File.Delete(archive);
            string driver = Path.Combine(temporary, "wintun.dll");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "wintun.dll"), driver);
            await Mihomo.CheckExecutableAsync(executable, release.Version, cancellationToken);
            await File.WriteAllLinesAsync(Path.Combine(temporary, "sha256.txt"),
                [HashFile(executable), HashFile(driver)], cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            string installed = DirectoryFor(target);
            if (Directory.Exists(installed))
            {
                if (!PlainDirectory(installed))
                    throw new IOException("Путь для ядра Mihomo занят ссылкой.");
                // A corrupt cache stays available for inspection while a clean copy replaces it.
                string old = Path.Combine(Root, ".mihomo-old-" + Guid.NewGuid().ToString("N"));
                Directory.Move(installed, old);
                try { Directory.Move(temporary, installed); }
                catch
                {
                    Directory.Move(old, installed);
                    throw;
                }
                TryDeleteDirectory(old);
            }
            else
                Directory.Move(temporary, installed);
            SaveSelection(target, cancellationToken);
        }
        finally { TryDeleteDirectory(temporary); }
    }

    private static async Task ExtractExecutableAsync(string archive, string asset, string destination,
        CancellationToken cancellationToken)
    {
        using var zip = ZipFile.OpenRead(archive);
        string versioned = Path.ChangeExtension(asset, ".exe");
        string unversioned = asset[..asset.LastIndexOf("-v", StringComparison.Ordinal)] + ".exe";
        var entries = zip.Entries.Where(entry => entry.FullName == versioned || entry.FullName == unversioned ||
            entry.FullName == "mihomo.exe").ToArray();
        if (entries.Length != 1 || entries[0].Length is <= 0 or > MaxExecutable)
            throw new IOException("В официальном архиве нет ожидаемого файла Mihomo для Windows x64.");

        await using var source = entries[0].Open();
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65536, useAsync: true);
        var buffer = new byte[65536];
        long copied = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            copied += count;
            if (copied > entries[0].Length)
                throw new IOException("Размер ядра Mihomo не совпадает с архивом.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        if (copied != entries[0].Length)
            throw new IOException("Архив Mihomo распакован не полностью.");
    }

    private static KernelSelection? ReadSelection()
    {
        try
        {
            if (!PlainDirectory(Root) || !PlainFile(SelectionPath) || new FileInfo(SelectionPath).Length > 2048)
                return null;
            var saved = JsonSerializer.Deserialize<KernelSelection>(File.ReadAllText(SelectionPath));
            return saved is not null && InstalledDirectory(saved) is not null ? saved : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void SaveSelection(KernelSelection target, CancellationToken cancellationToken)
    {
        string temporary = Path.Combine(Root, "selection-" + Guid.NewGuid().ToString("N") + ".new");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(target));
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, SelectionPath, overwrite: true);
            selection = target;
        }
        finally { File.Delete(temporary); }
    }

    private static string DirectoryFor(KernelSelection item) => Path.Combine(Root, $"mihomo-{item.Tag}-{item.ArchiveHash}");

    private static string? InstalledDirectory(KernelSelection item)
    {
        if (!TryVersion(item.Tag, out _) || item.ArchiveHash is not { Length: 64 } || !item.ArchiveHash.All(char.IsAsciiHexDigit) ||
            !PlainDirectory(Root))
            return null;
        string path = DirectoryFor(item);
        return PlainDirectory(path) && PlainFile(Path.Combine(path, "mihomo.exe")) &&
            PlainFile(Path.Combine(path, "wintun.dll")) ? path : null;
    }

    private static bool CheckCachedFiles(string path)
    {
        string metadata = Path.Combine(path, "sha256.txt");
        if (!PlainFile(metadata) || new FileInfo(metadata).Length > 256)
            return false;
        string[] hashes = File.ReadAllLines(metadata);
        return hashes.Length == 2 && hashes[0] == HashFile(Path.Combine(path, "mihomo.exe")) &&
            hashes[1] == HashFile(Path.Combine(path, "wintun.dll"));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool TryVersion(string? tag, out Version version)
    {
        version = BundledVersion;
        if (tag is not { Length: > 1 } || tag[0] != 'v' || !Version.TryParse(tag.AsSpan(1), out var parsed) ||
            parsed.Major != 1 || parsed.Build < 0 || parsed.Revision >= 0 || parsed < MinimumVersion ||
            tag != "v" + parsed.ToString(3))
            return false;
        version = parsed;
        return true;
    }

    private static bool PlainDirectory(string path) => HasExpectedAttributes(path, directory: true);
    private static bool PlainFile(string path) => HasExpectedAttributes(path, directory: false);

    private static bool HasExpectedAttributes(string path, bool directory)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return !attributes.HasFlag(FileAttributes.ReparsePoint) && attributes.HasFlag(FileAttributes.Directory) == directory;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!PlainDirectory(path))
            return;
        try { Directory.Delete(path, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
