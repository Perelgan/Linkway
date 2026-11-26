using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VPNDesktop;

internal sealed record GitHubRelease(UpdateVersion Version, Uri Download, byte[] Digest, long Size);

internal static partial class GitHubUpdates
{
    private const string Repository = "Perelgan/VPNDesktop";
    public const string ReleasesUrl = $"https://github.com/{Repository}/releases";
    private const string AssetName = "VPNDesktop-win-x64.zip";
    private const long MaxArchive = 256L * 1024 * 1024;
    private const long MaxExpanded = 512L * 1024 * 1024;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    private static string InstallRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    public static Version InstalledVersion { get; } =
        Version.Parse(typeof(App).Assembly.GetName().Version!.ToString(3));

    public static UpdateVersion InstalledRelease { get; } =
        ParseProductVersion(typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? new(InstalledVersion, null);

    public static async Task<GitHubRelease?> CheckAsync(bool experimental, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        (UpdateVersion Version, string Tag, JsonElement Asset)? latest = null;
        for (int page = 1; ; page++)
        {
            using var request = MakeRequest(new Uri($"https://api.github.com/repos/{Repository}/releases?per_page=100&page={page}"));
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new IOException("Репозиторий обновлений недоступен на GitHub.");
            if (!response.IsSuccessStatusCode)
                throw new IOException($"GitHub вернул HTTP {(int)response.StatusCode}.");

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token);
            var candidate = SelectRelease(json.RootElement, experimental, latest?.Version ?? InstalledRelease);
            if (candidate is { } newer)
                latest = (newer.Version, newer.Tag, newer.Asset.Clone());
            if (!response.Headers.TryGetValues("Link", out var links) || !links.Any(link => link.Contains("rel=\"next\"", StringComparison.Ordinal)))
                break;
        }

        if (latest is not { } selected)
            return null;
        var (version, tag, asset) = selected;
        string? digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
        if (digest is null || !DigestPattern().IsMatch(digest))
            throw new IOException("В карточке файла GitHub отсутствует SHA-256.");
        if (!asset.TryGetProperty("size", out var length) || !length.TryGetInt64(out long size) || size <= 0 || size > MaxArchive)
            throw new IOException("Размер файла обновления выходит за допустимый предел.");

        var url = new Uri($"{ReleasesUrl}/download/{tag}/{AssetName}");
        if (!string.Equals(asset.GetProperty("browser_download_url").GetString(), url.AbsoluteUri, StringComparison.Ordinal))
            throw new IOException("GitHub вернул неожиданный адрес файла обновления.");
        return new GitHubRelease(version, url, Convert.FromHexString(digest[7..]), size);
    }

    internal static (UpdateVersion Version, string Tag, JsonElement Asset)? SelectRelease(
        JsonElement releases, bool experimental, UpdateVersion installed)
    {
        (UpdateVersion Version, string Tag, JsonElement Asset)? latest = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean())
                continue;
            bool prerelease = release.GetProperty("prerelease").GetBoolean();
            string tag = release.GetProperty("tag_name").GetString() ?? "";
            if (UpdateVersion.Parse(tag) is not { } version || prerelease != (version.Prerelease is not null)
                || (!experimental && prerelease) || version.CompareTo(latest?.Version ?? installed) <= 0)
                continue;
            var asset = release.GetProperty("assets").EnumerateArray().FirstOrDefault(item =>
                item.GetProperty("name").GetString() == AssetName && item.GetProperty("state").GetString() == "uploaded");
            if (asset.ValueKind != JsonValueKind.Undefined)
                latest = (version, tag, asset);
        }
        return latest;
    }

    public static async Task<string> PrepareAsync(GitHubRelease release, IProgress<int> progress, CancellationToken cancellationToken)
    {
        string stage = Path.Combine(InstallRoot, ".vpn-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            string archivePath = Path.Combine(stage, AssetName);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            await DownloadAsync(release, archivePath, progress, deadline.Token);

            await Task.Run(() => Unpack(release, archivePath, stage, deadline.Token), deadline.Token);
            File.Delete(archivePath);
            File.Copy(Path.Combine(InstallRoot, "VPNDesktop.exe"), Path.Combine(stage, "updater.exe"));
            return stage;
        }
        catch
        {
            Discard(stage);
            throw;
        }
    }

    private static async Task DownloadAsync(GitHubRelease release, string path, IProgress<int> progress, CancellationToken token)
    {
        var url = release.Download;
        for (int redirect = 0; redirect < 5; redirect++)
        {
            using var request = MakeRequest(url);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
            {
                url = new Uri(url, next);
                if (!IsHttpsHost(url, "release-assets.githubusercontent.com") && !IsHttpsHost(url, "objects.githubusercontent.com"))
                    throw new IOException("Файл обновления перенаправлен на неизвестный сервер.");
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long declared && declared != release.Size)
                throw new IOException("Размер скачиваемого файла отличается от данных GitHub.");

            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[65536];
            long total = 0;
            int reported = -1;
            int count;
            while ((count = await input.ReadAsync(buffer, token)) != 0)
            {
                total += count;
                if (total > release.Size)
                    throw new IOException("Файл обновления больше размера, указанного GitHub.");
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), token);
                int percent = (int)(total * 100 / release.Size);
                if (percent != reported)
                    progress.Report(reported = percent);
            }
            if (total != release.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), release.Digest))
                throw new IOException("SHA-256 файла обновления не совпадает с данными GitHub.");
            return;
        }
        throw new IOException("Слишком много перенаправлений при загрузке обновления.");
    }

    private static void Unpack(GitHubRelease release, string archivePath, string stage, CancellationToken token)
    {
        string package = Path.Combine(stage, "package");
        Directory.CreateDirectory(package);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 4096)
            throw new IOException("В архиве обновления слишком много файлов.");

        long expanded = 0;
        byte[] buffer = new byte[65536];
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            string path = entry.FullName.Replace('\\', '/');
            var parts = path.TrimEnd('/').Split('/');
            if (parts[0] != "VPNDesktop" || parts.Any(InvalidPathPart)
                || (parts.Length > 1 && parts[1] is not ("VPNDesktop.exe" or "README.md" or "Libraries" or "Languages" or "Licenses"))
                || (parts.Length > 2 && parts[1] is ("VPNDesktop.exe" or "README.md"))
                || (entry.ExternalAttributes & 0x400) != 0 || (entry.ExternalAttributes >> 16 & 0xf000) == 0xa000)
                throw new IOException("Архив имеет неподдерживаемую структуру.");

            expanded += entry.Length;
            if (expanded > MaxExpanded)
                throw new IOException("Распакованный архив обновления слишком большой.");
            string destination = Path.Combine(package, Path.Combine(parts));
            if (path.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            long remaining = entry.Length;
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                remaining -= count;
                if (remaining < 0)
                    throw new IOException("Размер файла в архиве отличается от заявленного.");
                output.Write(buffer, 0, count);
            }
            if (remaining != 0)
                throw new IOException("Архив обновления неполный.");
        }

        string payload = Path.Combine(package, "VPNDesktop");
        foreach (string file in new[] { "VPNDesktop.exe", "README.md", "Libraries/VPNDesktop.App.exe",
                     "Libraries/VPNDesktop.dll", "Libraries/coreclr.dll", "Libraries/mihomo.exe", "Libraries/wintun.dll" })
            if (!File.Exists(Path.Combine(payload, file)))
                throw new IOException("В архиве нет полной сборки VPN Desktop.");
        if (!Directory.Exists(Path.Combine(payload, "Languages")) || !Directory.Exists(Path.Combine(payload, "Licenses")))
            throw new IOException("В архиве отсутствуют языковые ресурсы или лицензии.");
        var actualVersion = ParseProductVersion(FileVersionInfo.GetVersionInfo(Path.Combine(payload, "Libraries", "VPNDesktop.dll")).ProductVersion);
        if (actualVersion != release.Version)
            throw new IOException("Версия приложения внутри архива отличается от тега GitHub.");
    }

    private static bool InvalidPathPart(string part)
    {
        if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
            || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return true;
        string stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "NUL" or "CON" or "AUX" or "PRN" || ReservedPortPattern().IsMatch(stem);
    }

    public static void StartInstaller(string stage)
    {
        var start = new ProcessStartInfo(Path.Combine(stage, "updater.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = stage
        };
        start.ArgumentList.Add("--install-update");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить установщик обновления.");
    }

    public static void Discard(string stage)
    {
        if (Path.GetDirectoryName(stage) != InstallRoot || !StagePattern().IsMatch(Path.GetFileName(stage)))
            return;
        try { Directory.Delete(stage, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static HttpRequestMessage MakeRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd($"VPNDesktop/{InstalledVersion}");
        return request;
    }

    private static bool IsHttpsHost(Uri uri, string host) => uri.Scheme == "https" && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase);

    private static UpdateVersion? ParseProductVersion(string? version) =>
        version is null ? null : UpdateVersion.Parse(version.Split('+', 2)[0]);

    [GeneratedRegex(@"\Asha256:[a-fA-F0-9]{64}\z")]
    private static partial Regex DigestPattern();
    [GeneratedRegex(@"\A(?:COM|LPT)[1-9¹²³]\z")]
    private static partial Regex ReservedPortPattern();
    [GeneratedRegex(@"\A\.vpn-update-[a-f0-9]{32}\z")]
    private static partial Regex StagePattern();
}
