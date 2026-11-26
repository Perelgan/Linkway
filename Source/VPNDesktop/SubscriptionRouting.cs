using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace VPNDesktop;

internal sealed record SubscriptionGroup(string Name, string[] Members, string? Preferred, bool IncludeNodes,
    string Type, bool Hidden)
{
    public string[] Options(Profile source) => Members.Concat(IncludeNodes
            ? source.Nodes.Where(RoutingConfig.IsRoutable).Select(node => node.Name)
            : [])
        .Distinct(StringComparer.Ordinal).ToArray();

    public string? Default(Profile source)
    {
        var options = Options(source);
        return Preferred is not null && options.Contains(Preferred, StringComparer.Ordinal) ? Preferred : options.FirstOrDefault();
    }
}

internal sealed record SubscriptionRule(string Match, string Destination, string Modifier);

internal sealed record SubscriptionFeed(string Name, string Behavior, string Format, Uri? Address, string[]? Payload);

internal sealed partial class SubscriptionRouting
{
    private sealed record Result(SubscriptionRouting? Value, string? Error = null);
    private static readonly ConditionalWeakTable<Profile, Result> Cache = new();

    public IReadOnlyDictionary<string, SubscriptionGroup> Groups { get; }
    public IReadOnlyDictionary<string, SubscriptionFeed> Feeds { get; }
    public IReadOnlyDictionary<string, string> Policies { get; }
    public SubscriptionRule[] Rules { get; }
    private readonly HashSet<string> visibleGroups;

    private SubscriptionRouting(Dictionary<string, SubscriptionGroup> groups, Dictionary<string, SubscriptionFeed> feeds,
        Dictionary<string, string> policies, SubscriptionRule[] rules)
    {
        Groups = groups;
        Feeds = feeds;
        Policies = policies;
        Rules = rules;
        var destinations = rules.TakeWhile(rule => rule.Match != "MATCH")
            .Concat(rules.Where(rule => rule.Match == "MATCH").Take(1))
            .Select(rule => rule.Destination).ToHashSet(StringComparer.Ordinal);
        visibleGroups = groups.Values.Where(group => !group.Hidden && group.Type is "" or "select" &&
            !group.Name.Equals("PROXY", StringComparison.OrdinalIgnoreCase) &&
            !group.Name.Equals("GLOBAL", StringComparison.OrdinalIgnoreCase) &&
            destinations.Contains(group.Name)).Select(group => group.Name).ToHashSet(StringComparer.Ordinal);
    }

    public bool IsVisibleGroup(string name) => visibleGroups.Contains(name);

    public bool IsServiceGroup(string name) => Groups.ContainsKey(name) && !IsVisibleGroup(name);

    public static SubscriptionRouting? Read(Profile profile)
    {
        var result = Cache.GetValue(profile, source =>
        {
            try { return new Result(Parse(source.Content)); }
            catch (Exception error) when (error is YamlException or ArgumentException or InvalidOperationException or IOException)
            {
                return new Result(null, error is IOException ? error.Message : "Не удалось прочитать группы и правила подписки.");
            }
        });
        if (result.Error is { } failure)
            throw new IOException(failure);
        return result.Value;
    }

    public static bool HasRules(string content)
    {
        try { return Parse(content) is not null; }
        catch (Exception error) when (error is YamlException or ArgumentException or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static SubscriptionRouting? Parse(string content)
    {
        if (!MayHaveRouting(content))
        {
            string encoded = content.Trim();
            if (encoded.Length == 0 || encoded.Length % 4 != 0 ||
                !encoded.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))
                return null;
            try { content = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); }
            catch (FormatException) { return null; }
            if (!MayHaveRouting(content))
                return null;
        }

        var yaml = new YamlStream();
        using var reader = new StringReader(content);
        yaml.Load(new LimitedParser(new Parser(reader)));
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new IOException("В подписке ожидается одна конфигурация Mihomo.");

        var groups = new Dictionary<string, SubscriptionGroup>(StringComparer.Ordinal);
        foreach (var group in Sequence(root, "proxy-groups").OfType<YamlMappingNode>())
        {
            string name = ReadString(group, "name");
            if (name.Length is 0 or > 1024 || name.Any(char.IsControl))
                throw new IOException("Группа подписки имеет некорректное название.");
            var members = Sequence(group, "proxies").Select(Scalar).Where(value => value.Length != 0).ToArray();
            string preferred = ReadString(group, "default-selected");
            groups.Add(name, new(name, members, preferred.Length == 0 ? null : preferred,
                IsEnabled(group, "include-all") || IsEnabled(group, "include-all-proxies"),
                ReadString(group, "type").ToLowerInvariant(), IsEnabled(group, "hidden")));
        }
        var feeds = new Dictionary<string, SubscriptionFeed>(StringComparer.Ordinal);
        if (Child(root, "rule-providers") is YamlMappingNode providers)
            foreach (var (key, value) in providers.Children)
                if (value is YamlMappingNode feed)
                {
                    string name = Scalar(key);
                    string type = ReadString(feed, "type").ToLowerInvariant();
                    string behavior = ReadString(feed, "behavior").ToLowerInvariant();
                    string format = ReadString(feed, "format").ToLowerInvariant();
                    Uri? address = null;
                    if (type == "http")
                        Uri.TryCreate(ReadString(feed, "url"), UriKind.Absolute, out address);
                    feeds.Add(name, new(name, behavior, format.Length == 0 ? "yaml" : format, address,
                        type == "inline" ? Sequence(feed, "payload").Select(Scalar).ToArray() : null));
                }

        var rules = Sequence(root, "rules").Select(Scalar).Select(ParseRule).ToArray();
        var policies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Sequence(root, "proxies").OfType<YamlMappingNode>())
        {
            string type = ReadString(entry, "type").ToLowerInvariant();
            if (type is "direct" or "reject")
                policies.TryAdd(ReadString(entry, "name"), type == "direct" ? "DIRECT" : "REJECT");
        }
        return rules.Length == 0 ? null : new(groups, feeds, policies, rules);
    }

    private static bool MayHaveRouting(string content) =>
        content.Contains("rules", StringComparison.Ordinal) && RoutingSectionPattern().IsMatch(content);

    [GeneratedRegex("""(?m)(?:^|[,{])\s*['"]?rules['"]?\s*:""")]
    private static partial Regex RoutingSectionPattern();

    private static YamlNode? Child(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private static IEnumerable<YamlNode> Sequence(YamlMappingNode map, string key) =>
        Child(map, key) is YamlSequenceNode items ? items.Children : [];

    private static string ReadString(YamlMappingNode map, string key) => Scalar(Child(map, key));
    private static string Scalar(YamlNode? value) => (value as YamlScalarNode)?.Value?.Trim() ?? "";
    private static bool IsEnabled(YamlMappingNode map, string key) =>
        ReadString(map, key).Equals("true", StringComparison.OrdinalIgnoreCase);

    public static SubscriptionRule ParseRule(string rule)
    {
        if (rule.Length == 0 || rule.Any(char.IsControl))
            throw new IOException("В подписке есть пустое или некорректное правило.");

        int separator = rule.IndexOf(',');
        string kind = separator < 0 ? rule : rule[..separator].Trim();
        if (kind == "SUB-RULE")
            throw new IOException("Подписка использует вложенные правила SUB-RULE, которые пока не поддерживаются.");
        bool logical = kind is "AND" or "OR" or "NOT";
        var fields = new List<string>();
        int depth = 0, start = 0;
        for (int index = 0; index < rule.Length; index++)
        {
            switch (rule[index])
            {
                case '(' when logical: depth++; break;
                case ')' when logical: depth--; break;
                case ',' when depth == 0:
                    fields.Add(rule[start..index].Trim());
                    start = index + 1;
                    break;
            }
        }
        fields.Add(rule[start..].Trim());
        int required = kind == "MATCH" ? 2 : 3;
        if (depth != 0 || fields.Count < required || fields.Any(string.IsNullOrEmpty))
            throw new IOException($"В подписке есть некорректное правило {kind}.");
        string match = required == 2 ? "MATCH" : $"{kind},{fields[1]}";
        return new(match, fields[required - 1],
            fields.Count > required ? "," + string.Join(",", fields.Skip(required)) : "");
    }

    public static string RewriteFeedReferences(string match, MatchEvaluator evaluate) => FeedReferences().Replace(match, evaluate);

    public static bool IsLocalOption(string choice) => choice is "DIRECT" or "REJECT" or RoutingConfig.Main;

    [GeneratedRegex(@"(?<prefix>^|\()\s*RULE-SET\s*,\s*(?<name>[^,()]+)")]
    private static partial Regex FeedReferences();

    private sealed class LimitedParser(IParser parser) : IParser
    {
        private int depth;
        public ParsingEvent? Current => parser.Current;

        public bool MoveNext()
        {
            if (!parser.MoveNext())
                return false;
            if (Current is MappingStart or SequenceStart && ++depth > 64)
                throw new IOException("Слишком глубокая вложенность YAML-подписки.");
            if (Current is MappingEnd or SequenceEnd)
                depth--;
            return true;
        }
    }
}

