using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VPNDesktop;

internal enum RouteMatch { Application, Domain, DomainSuffix, Address, Subnet, Port, GeoIp, GeoSite }
internal enum RouteProtocol { Any, Tcp, Udp }
internal enum RouteKind { Main, Direct, Reject, Server, Group, SubscriptionGroup }

internal sealed record RouteServer(string Source, string Name)
{
    public static string SourceKey(Profile source) => source.CabinetPlanId is long plan
        ? $"cabinet-{plan.ToString(CultureInfo.InvariantCulture)}" : source.Id.ToString("N");

    public static RouteServer From(ServerChoice server) => new(SourceKey(server.Source), server.Node.Name);
}

internal sealed record RouteTarget(RouteKind Kind, RouteServer? Server = null, Guid? Group = null,
    SubscriptionGroupRef? Category = null);
internal sealed record RouteRule(RouteMatch Match, string Value, RouteProtocol Protocol, RouteTarget Target,
    [property: JsonRequired] bool Enabled = true);
internal sealed record RouteGroup(Guid Id, string Name, RouteServer[] Servers);
internal sealed record SubscriptionGroupRef(string Source, string Name);
internal sealed record SubscriptionChoice(string Source, string Group, string? Destination, RouteTarget? Target = null);
internal sealed record RoutingProfile(Guid Id, string Name, RouteTarget Default, RouteRule[] Rules, RouteGroup[] Groups,
    SubscriptionChoice[]? SubscriptionChoices = null, bool UseSubscriptionRules = true, bool PreferLocalFallback = false)
{
    [JsonIgnore]
    public bool UseSubscriptionFallback => !PreferLocalFallback && Default.Kind == RouteKind.Main;

    public static RoutingProfile Create(string name) => new(Guid.NewGuid(), name, new(RouteKind.Main), [], []);
}

internal sealed record RoutingLibrary(RoutingProfile[] Profiles, RoutingProfile? Active)
{
    public static RoutingLibrary Create() => new([RoutingProfile.Create("Основной")], null);
}

internal static partial class RoutingProfiles
{
    private const int MaxFileSize = 2 * 1024 * 1024;
    private const int Version = 1;
    private static readonly string StorePath = Path.Combine(ProfileStore.DirectoryPath, "routing.bin");
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private sealed record ProfileFile(int Version, RoutingProfile Profile);
    private sealed record LibraryFile(int Version, RoutingLibrary Library);

    public static RoutingLibrary Load()
    {
        if (!File.Exists(StorePath))
            return RoutingLibrary.Create();
        byte[] clear = ProtectedData.Unprotect(ReadBytes(StorePath), null, DataProtectionScope.CurrentUser);
        try
        {
            var file = JsonSerializer.Deserialize<LibraryFile>(clear, Json);
            if (file is not { Version: Version, Library.Profiles.Length: > 0 and <= 100 } ||
                file.Library.Profiles.Any(p => p is null) ||
                file.Library.Profiles.Select(p => p.Id).Distinct().Count() != file.Library.Profiles.Length)
                throw new IOException("Хранилище маршрутизации повреждено или имеет неподдерживаемую версию.");
            return new(file.Library.Profiles.Select(Upgrade).ToArray(),
                file.Library.Active is { } active ? Upgrade(active) : null);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public static void Save(RoutingLibrary library)
    {
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(new LibraryFile(Version, library), Json);
        try
        {
            byte[] encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
            if (encrypted.Length > MaxFileSize)
                throw new IOException("Профили маршрутизации превышают допустимый размер.");
            File.WriteAllBytes(StorePath + ".new", encrypted);
            File.Move(StorePath + ".new", StorePath, true);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public static RoutingProfile Import(string path)
    {
        try
        {
            var file = JsonSerializer.Deserialize<ProfileFile>(ReadBytes(path), Json);
            if (file is not { Version: Version, Profile: not null })
                throw new IOException("Неподдерживаемая версия профиля маршрутизации.");
            return Upgrade(file.Profile) with { Id = Guid.NewGuid() };
        }
        catch (JsonException)
        {
            throw new IOException("Файл не соответствует формату профиля маршрутизации VPN Desktop.");
        }
    }

    public static void Export(string path, RoutingProfile profile) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new ProfileFile(Version, profile), Json));

    public static RoutingProfile Upgrade(RoutingProfile profile)
    {
        profile = Validate(profile);
        if (profile.Groups.Length == 0)
            return profile;
        var firstServers = profile.Groups.ToDictionary(group => group.Id, group => group.Servers[0]);
        return profile with
        {
            Default = ReplaceGroup(profile.Default),
            Rules = profile.Rules.Select(rule => rule with { Target = ReplaceGroup(rule.Target) }).ToArray(),
            SubscriptionChoices = profile.SubscriptionChoices?.Select(choice => choice.Target is { } target
                ? choice with { Target = ReplaceGroup(target) } : choice).ToArray(),
            Groups = []
        };

        RouteTarget ReplaceGroup(RouteTarget target) => target is { Kind: RouteKind.Group, Group: Guid id }
            ? new(RouteKind.Server, firstServers[id]) : target;
    }

    private static byte[] ReadBytes(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxFileSize)
            throw new IOException("Размер файла маршрутизации превышает 2 МБ.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    public static RoutingProfile Validate(RoutingProfile profile)
    {
        if (profile.Id == Guid.Empty || !ValidName(profile.Name) ||
            profile.Rules is not { Length: <= 1000 } || profile.Groups is not { Length: <= 100 })
            throw new IOException("Проверьте название, правила и группы профиля.");
        if (profile.SubscriptionChoices is { } choices && (choices.Length > 1000 ||
            choices.Any(choice => choice is null || !ValidServer(new(choice.Source, choice.Group)) ||
                (choice.Target is null ? choice.Destination is null || !ValidServer(new(choice.Source, choice.Destination))
                    : choice.Destination is not null || choice.Target.Kind is not (RouteKind.Server or RouteKind.Group))) ||
            choices.Select(choice => (choice.Source, choice.Group)).Distinct().Count() != choices.Length))
            throw new IOException("Проверьте выбранные маршруты групп подписки.");

        var ids = new HashSet<Guid>();
        foreach (var group in profile.Groups)
        {
            if (group is null || group.Id == Guid.Empty || !ids.Add(group.Id) ||
                !ValidName(group.Name) || group.Servers is not { Length: > 0 and <= 100 } ||
                group.Servers.Any(server => !ValidServer(server)) ||
                group.Servers.Distinct().Count() != group.Servers.Length)
                throw new IOException("Каждой группе нужны уникальный идентификатор, название и список серверов без повторов.");
        }

        ValidateTarget(profile.Default);
        foreach (var choice in profile.SubscriptionChoices ?? [])
            if (choice.Target is { } target)
                ValidateTarget(target);
        return profile with
        {
            Rules = profile.Rules.Select(rule =>
            {
                if (rule is null || !Enum.IsDefined(rule.Protocol))
                    throw new IOException("Неизвестный протокол правила.");
                ValidateTarget(rule.Target);
                return rule with { Value = Normalize(rule.Match, rule.Value) };
            }).ToArray()
        };

        void ValidateTarget(RouteTarget? target)
        {
            bool valid = target?.Kind switch
            {
                RouteKind.Main or RouteKind.Direct or RouteKind.Reject =>
                    target.Server is null && target.Group is null && target.Category is null,
                RouteKind.Server => ValidServer(target.Server) && target.Group is null && target.Category is null,
                RouteKind.Group => target.Server is null && target.Group is Guid id && ids.Contains(id) && target.Category is null,
                RouteKind.SubscriptionGroup => target.Server is null && target.Group is null &&
                    target.Category is { } category && ValidServer(new(category.Source, category.Name)),
                _ => false
            };
            if (!valid)
                throw new IOException("У правила или маршрута по умолчанию некорректное направление.");
        }
    }

    private static bool ValidName(string? value) =>
        value is { Length: > 0 and <= 100 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    private static bool ValidServer(RouteServer? server) =>
        server?.Source is { Length: > 0 and <= 64 } source && SourcePattern().IsMatch(source) &&
        server.Name is { Length: > 0 and <= 1024 } && !server.Name.Any(char.IsControl);

    public static string Normalize(RouteMatch match, string? input)
    {
        string value = input?.Trim() ?? "";
        if (value.Length is 0 or > 1024 || value.Any(char.IsControl) || (match != RouteMatch.Application && value.Contains(',')))
            throw new IOException("Введите корректное значение правила.");

        switch (match)
        {
            case RouteMatch.Application:
                if (Path.IsPathFullyQualified(value) && !value.StartsWith(@"\\?\", StringComparison.Ordinal) &&
                    !value.StartsWith(@"\\.\", StringComparison.Ordinal) &&
                    Path.GetExtension(value).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    try { return Path.GetFullPath(value); }
                    catch (Exception e) when (e is ArgumentException or NotSupportedException) { break; }
                }
                break;
            case RouteMatch.Domain:
            case RouteMatch.DomainSuffix:
                value = value.TrimEnd('.');
                if (match == RouteMatch.DomainSuffix)
                    value = value.StartsWith("*.", StringComparison.Ordinal) ? value[2..] : value.TrimStart('.');
                try
                {
                    string ascii = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(value);
                    if (Uri.CheckHostName(ascii) == UriHostNameType.Dns && ascii.Length <= 253)
                        return ascii.ToLowerInvariant();
                }
                catch (ArgumentException) { }
                break;
            case RouteMatch.Address:
                if (!value.Contains('%') && IPAddress.TryParse(value, out var ip) &&
                    (value.Contains(':') || value == ip.ToString()))
                    return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
                break;
            case RouteMatch.Subnet:
                if (!value.Contains('%') && IPNetwork.TryParse(value, out var network) && !network.BaseAddress.IsIPv4MappedToIPv6)
                    return network.ToString();
                break;
            case RouteMatch.Port:
                string[] range = value.Split('-');
                if (range.Length is 1 or 2 && range.All(p => ushort.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port > 0) &&
                    ushort.Parse(range[0], CultureInfo.InvariantCulture) <= ushort.Parse(range[^1], CultureInfo.InvariantCulture))
                    return string.Join("-", range.Select(p => ushort.Parse(p, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)));
                break;
            case RouteMatch.GeoIp:
                if (CountryPattern().IsMatch(value))
                    return value.ToLowerInvariant();
                break;
            case RouteMatch.GeoSite:
                if (CategoryPattern().IsMatch(value))
                    return value.ToLowerInvariant();
                break;
        }
        throw new IOException("Проверьте значение. Примеры: C:\\Apps\\app.exe, example.org, 2001:db8::1, 10.0.0.0/8, 443, 8000-8100, ru, category-ads-all.");
    }

    [GeneratedRegex(@"\A(?:[a-f0-9]{32}|cabinet-[0-9]+)\z")]
    private static partial Regex SourcePattern();
    [GeneratedRegex(@"\A(?:[a-zA-Z]{2}|private)\z", RegexOptions.IgnoreCase)]
    private static partial Regex CountryPattern();
    [GeneratedRegex(@"\A[a-zA-Z0-9_-]+(?:@[a-zA-Z0-9_-]+)*\z")]
    private static partial Regex CategoryPattern();
}

