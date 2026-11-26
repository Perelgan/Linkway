using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace VPNDesktop;

public sealed class CountryFlagLabelConverter : IValueConverter
{
    private const string AssetName = "VPNDesktop.CountryFlags.zip";
    private const int FirstRegionalIndicator = 0x1F1E6;
    private static readonly Dictionary<string, BitmapSource?> Flags = new(StringComparer.Ordinal);

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var (flag, label) = Split(value as string ?? "");
        return Equals(parameter, "Flag") ? flag : label;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static (BitmapSource? Flag, string Label) Split(string label)
    {
        const string automatic = "По подписке · ";
        int start = label.StartsWith(automatic, StringComparison.Ordinal) ? automatic.Length : 0;
        ReadOnlySpan<char> name = label.AsSpan(start).TrimStart();
        if (name.Length < 2)
            return (null, label);

        char first, second;
        int length;
        if (name.Length >= 4 && TryRegionalIndicator(name, out first) && TryRegionalIndicator(name[2..], out second))
            length = 4;
        else if (name.Length > 2 && char.IsAsciiLetterUpper(name[0]) && char.IsAsciiLetterUpper(name[1]) &&
            (char.IsWhiteSpace(name[2]) || name[2] is '-' or '–' or '—' or '·' or '|' or ':'))
        {
            first = char.ToLowerInvariant(name[0]);
            second = char.ToLowerInvariant(name[1]);
            length = 2;
        }
        else
            return (null, label);

        var flag = LoadFlag(string.Concat(first, second));
        if (flag is null)
            return (null, label);
        name = name[length..].TrimStart();
        while (!name.IsEmpty && name[0] is '-' or '–' or '—' or '·' or '|' or ':' or '\uFE0F')
            name = name[1..].TrimStart();
        return (flag, label[..start] + (name.IsEmpty ? $"{char.ToUpperInvariant(first)}{char.ToUpperInvariant(second)}" : name.ToString()));
    }

    private static bool TryRegionalIndicator(ReadOnlySpan<char> text, out char letter)
    {
        letter = '\0';
        if (!char.IsSurrogatePair(text[0], text[1]))
            return false;
        int offset = char.ConvertToUtf32(text[0], text[1]) - FirstRegionalIndicator;
        if (offset is < 0 or >= 26)
            return false;
        letter = (char)('a' + offset);
        return true;
    }

    private static BitmapSource? LoadFlag(string code)
    {
        lock (Flags)
        {
            if (Flags.TryGetValue(code, out var image))
                return image;
            using var asset = typeof(CountryFlagLabelConverter).Assembly.GetManifestResourceStream(AssetName)!;
            using var archive = new ZipArchive(asset, ZipArchiveMode.Read);
            if (archive.GetEntry(code + ".png") is { } entry)
            {
                using var bytes = entry.Open();
                using var buffer = new MemoryStream();
                bytes.CopyTo(buffer);
                buffer.Position = 0;
                image = BitmapFrame.Create(buffer, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                image.Freeze();
            }
            Flags.Add(code, image);
            return image;
        }
    }
}
