using System.Text.RegularExpressions;

namespace VPNDesktop;

internal sealed partial record UpdateVersion(Version Number, string? Prerelease) : IComparable<UpdateVersion>
{
    public static UpdateVersion? Parse(string value)
    {
        var match = VersionPattern().Match(value);
        return match.Success && Version.TryParse(match.Groups[1].Value, out var number)
            ? new(number, match.Groups[2].Success ? match.Groups[2].Value : null)
            : null;
    }

    public int CompareTo(UpdateVersion? other)
    {
        if (other is null)
            return 1;
        int number = Number.CompareTo(other.Number);
        if (number != 0 || Prerelease == other.Prerelease)
            return number;
        if (Prerelease is null || other.Prerelease is null)
            return Prerelease is null ? 1 : -1;

        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            bool leftNumeric = left[i].All(char.IsAsciiDigit);
            bool rightNumeric = right[i].All(char.IsAsciiDigit);
            if (leftNumeric != rightNumeric)
                return leftNumeric ? -1 : 1;
            if (leftNumeric && left[i].Length != right[i].Length)
                return left[i].Length.CompareTo(right[i].Length);
            int order = string.CompareOrdinal(left[i], right[i]);
            if (order != 0)
                return order;
        }
        return left.Length.CompareTo(right.Length);
    }

    public override string ToString() => Prerelease is null ? Number.ToString(3) : $"{Number.ToString(3)}-{Prerelease}";

    [GeneratedRegex(@"\Av?((?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?\z", RegexOptions.NonBacktracking)]
    private static partial Regex VersionPattern();
}
