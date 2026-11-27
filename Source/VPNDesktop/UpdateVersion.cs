using System.Globalization;
using System.Text.RegularExpressions;

namespace VPNDesktop;

internal sealed partial record UpdateVersion(Version Number, string? Prerelease) : IComparable<UpdateVersion>
{
    private const int LastBetaBeforeStable14 = 18;
    private bool IsSeries14 => Number is { Major: 1, Minor: 4 };

    private int? BetaNumber
    {
        get
        {
            if (Prerelease is not { } label || !label.StartsWith("beta.", StringComparison.Ordinal) ||
                !int.TryParse(label.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int beta))
                return null;
            if (!IsSeries14)
                return beta;
            // Each listed build occupies one place in the published 1.4 timeline.
            return (Number.Build, beta) switch
            {
                (1, 1) => 19,
                (2, 1) => 20,
                (3, 1) => 21,
                (0, 19) => 22,
                (4, 20) => 23,
                _ => beta
            };
        }
    }

    public string DisplayName
    {
        get
        {
            string series = Number.ToString(2);
            if (BetaNumber is { } beta)
                return $"{series}-beta.{beta}";
            if (Prerelease is not null)
                return $"{series}-{Prerelease}";
            return Number is { Major: 1, Minor: 3, Build: >= 0 and < 3 }
                ? $"{series} · архив {Number.Build + 1}" : series;
        }
    }

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
        int major = Number.Major.CompareTo(other.Number.Major);
        if (major != 0)
            return major;
        int minor = Number.Minor.CompareTo(other.Number.Minor);
        if (minor != 0)
            return minor;
        if (IsSeries14)
        {
            int order = Series14Order().CompareTo(other.Series14Order());
            if (order != 0)
                return order;
        }
        int patch = Math.Max(Number.Build, 0).CompareTo(Math.Max(other.Number.Build, 0));
        if (patch != 0)
            return patch;
        if (Prerelease == other.Prerelease)
            return other.Number.Build.CompareTo(Number.Build);
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

    private long Series14Order() => BetaNumber is { } beta ? 2L * beta
        : Prerelease is null ? 2L * LastBetaBeforeStable14 + 1 : -1;

    public override string ToString()
    {
        string number = Number.ToString(Number.Build < 0 ? 2 : 3);
        return Prerelease is null ? number : $"{number}-{Prerelease}";
    }

    [GeneratedRegex(@"\Av?((?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*))?)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?\z", RegexOptions.NonBacktracking)]
    private static partial Regex VersionPattern();
}
