using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace VPNDesktop;

internal sealed partial record TrafficApplication(string Key, string Name, BitmapSource? Icon = null)
{
    internal static TrafficApplication Resolve(string process, string path)
    {
        string executable = System.IO.Path.GetFileName(path.Length == 0 ? process : path);
        string fallback = System.IO.Path.GetFileNameWithoutExtension(executable);
        if (string.IsNullOrWhiteSpace(fallback))
            return new("unknown", "Не определено");
        var basic = new TrafficApplication("process:" + fallback, FriendlyName(fallback));

        // Only read resources from a local executable. Network paths can block the sampler.
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return basic;

        var icon = ApplicationPickerEntry.ReadIcon(path);
        FileVersionInfo metadata;
        try { metadata = FileVersionInfo.GetVersionInfo(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return basic with { Icon = icon };
        }

        string product = Clean(metadata.ProductName);
        string company = Clean(metadata.CompanyName);
        string description = Clean(metadata.FileDescription);
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (directory is null)
            return basic with { Icon = icon };

        if (WindowsPackage().Match(directory) is { Success: true } package)
        {
            string name = product.Length > 0 && !IsWindowsProduct(product) ? product : description;
            return new("package:" + package.Groups["name"].Value + "_" + package.Groups["publisher"].Value,
                name.Length > 0 ? name : basic.Name, icon);
        }

        if (IsWindowsProduct(product))
            return basic with { Name = description.Length > 0 ? description : basic.Name, Icon = icon };

        if (product.Length == 0)
            return basic with { Name = description.Length > 0 ? description : basic.Name, Icon = icon };

        return new("product:" + (company.Length > 0 ? company : directory) + "|" + product, product, icon);
    }

    private static bool IsWindowsProduct(string value) =>
        value.Contains("Windows", StringComparison.OrdinalIgnoreCase) &&
        (value.Contains("Operating System", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) && value.Contains("Windows NT", StringComparison.OrdinalIgnoreCase));

    private static string FriendlyName(string name) => name.ToLowerInvariant() switch
    {
        "svchost" => "Службы Windows",
        "explorer" => "Проводник Windows",
        "msedgewebview2" => "Microsoft Edge WebView2",
        "vpndesktop.app" or "linkway.session" => "Linkway",
        "koala-mihomo" => "Koala Clash",
        _ => name.Replace('_', ' ')
    };

    private static string Clean(string? value) => value is null ? "" : new string(value
        .Where(character => !char.IsControl(character)).Take(120).ToArray()).Trim();

    [GeneratedRegex(@"(?:^|\\)WindowsApps\\(?<name>[^\\]+?)_\d+(?:\.\d+){3}_[^_\\]+_[^_\\]*_(?<publisher>[^\\]+)(?:\\|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPackage();
}

internal sealed record TrafficRow(string Name, long Download, long Upload, BitmapSource? Icon = null)
{
    public string Amount => $"↓ {Format(Download)}     ↑ {Format(Upload)}";
    public string Details => $"{Name}\nПолучено: {Download:N0} Б\nОтправлено: {Upload:N0} Б";

    private static string Format(long bytes) => bytes switch
    {
        >= 1073741824 => $"{bytes / 1073741824d:0.##} ГиБ",
        >= 1048576 => $"{bytes / 1048576d:0.##} МиБ",
        >= 1024 => $"{bytes / 1024d:0.##} КиБ",
        _ => $"{bytes:N0} Б"
    };
}
