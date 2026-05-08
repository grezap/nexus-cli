using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Inventory;

/// <summary>
/// Hand-rolled flow-mapping reader for nexus-platform-plan/docs/infra/vms.yaml.
/// BCL-only, AOT-clean. Recognises the canonical file shape: top-level
/// <c>clusters:</c> blocks (one or more); <c>&lt;name&gt;:</c> at indent 2;
/// <c>purpose</c>/<c>phase</c>/<c>nodes</c> fields at indent 4; node entries
/// as single-line flow mappings <c>- { key: val, ... }</c> at indent 6.
/// Multiple <c>clusters:</c> roots are merged in file order. ADR-0006.
/// </summary>
public sealed class VmsYamlCatalog : IVmsCatalog
{
    public const string PathEnvVar = "NEXUS_VMS_YAML";

    private readonly string? _explicitPath;
    private IReadOnlyDictionary<string, ClusterRecord>? _cache;
    private string? _loadError;

    public VmsYamlCatalog(string? explicitPath = null) => _explicitPath = explicitPath;

    public Result<IReadOnlyDictionary<string, ClusterRecord>> Load()
    {
        if (_cache is not null)
            return Result.Ok(_cache);
        if (_loadError is not null)
            return Result.Fail<IReadOnlyDictionary<string, ClusterRecord>>(_loadError);

        var path = ResolvePath();
        if (path is null)
        {
            _loadError = $"vms.yaml not found. Set {PathEnvVar} to its path, or run from a checkout sibling to nexus-platform-plan/.";
            return Result.Fail<IReadOnlyDictionary<string, ClusterRecord>>(_loadError);
        }

        try
        {
            var lines = File.ReadAllLines(path);
            _cache = Parse(lines);
            return Result.Ok(_cache);
        }
        catch (Exception ex)
        {
            _loadError = $"failed to read {path}: {ex.Message}";
            return Result.Fail<IReadOnlyDictionary<string, ClusterRecord>>(_loadError);
        }
    }

    public Result<ClusterRecord> GetCluster(string name)
    {
        var loaded = Load();
        if (loaded.IsFail)
            return Result.Fail<ClusterRecord>(loaded.Error!);
        if (loaded.Value!.TryGetValue(name, out var cluster))
            return Result.Ok(cluster);
        var known = string.Join(", ", loaded.Value.Keys.OrderBy(k => k, StringComparer.Ordinal));
        return Result.Fail<ClusterRecord>($"unknown cluster '{name}'. Known: {known}");
    }

    private string? ResolvePath()
    {
        if (!string.IsNullOrWhiteSpace(_explicitPath) && File.Exists(_explicitPath))
            return _explicitPath;
        var env = Environment.GetEnvironmentVariable(PathEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;
        var sibling = Path.GetFullPath(Path.Combine(
            Environment.CurrentDirectory,
            "..", "nexus-platform-plan", "docs", "infra", "vms.yaml"));
        return File.Exists(sibling) ? sibling : null;
    }

    internal static IReadOnlyDictionary<string, ClusterRecord> Parse(string[] lines)
    {
        var result = new Dictionary<string, ClusterRecord>(StringComparer.Ordinal);
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || IsComment(line))
            {
                i++;
                continue;
            }
            if (Indent(line) == 0 && line.TrimEnd() == "clusters:")
            {
                i++;
                ParseClustersBlock(lines, ref i, result);
                continue;
            }
            i++;
        }
        return result;
    }

    private static void ParseClustersBlock(string[] lines, ref int i, Dictionary<string, ClusterRecord> sink)
    {
        while (i < lines.Length)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || IsComment(line))
            {
                i++;
                continue;
            }
            var indent = Indent(line);
            if (indent == 0)
            {
                // Hit another top-level key — close this clusters block.
                return;
            }
            if (indent == 2)
            {
                var content = line.TrimStart().TrimEnd();
                if (content.EndsWith(':'))
                {
                    var name = content[..^1].Trim();
                    i++;
                    sink[name] = ParseCluster(name, lines, ref i);
                    continue;
                }
            }
            i++;
        }
    }

    private static ClusterRecord ParseCluster(string name, string[] lines, ref int i)
    {
        var purpose = string.Empty;
        var phase = string.Empty;
        var nodes = new List<NodeRecord>();

        while (i < lines.Length)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || IsComment(line))
            {
                i++;
                continue;
            }
            var indent = Indent(line);
            if (indent <= 2)
                break;

            if (indent == 4)
            {
                var content = line.TrimStart();
                if (content.StartsWith("purpose:", StringComparison.Ordinal))
                {
                    purpose = StripValue(content["purpose:".Length..]);
                    i++;
                    continue;
                }
                if (content.StartsWith("phase:", StringComparison.Ordinal))
                {
                    phase = StripValue(content["phase:".Length..]);
                    i++;
                    continue;
                }
                if (content.TrimEnd() == "nodes:")
                {
                    i++;
                    while (i < lines.Length)
                    {
                        var nodeLine = lines[i];
                        if (string.IsNullOrWhiteSpace(nodeLine) || IsComment(nodeLine))
                        {
                            i++;
                            continue;
                        }
                        if (Indent(nodeLine) <= 4)
                            break;
                        var nl = nodeLine.TrimStart();
                        if (nl.StartsWith("- {", StringComparison.Ordinal))
                        {
                            var node = ParseNodeFlowMapping(nl);
                            if (node is not null)
                                nodes.Add(node);
                        }
                        i++;
                    }
                    continue;
                }

                // Skip an unrelated field plus any sub-block indented past 4.
                i++;
                while (i < lines.Length)
                {
                    var sub = lines[i];
                    if (string.IsNullOrWhiteSpace(sub) || IsComment(sub))
                    {
                        i++;
                        continue;
                    }
                    if (Indent(sub) <= 4)
                        break;
                    i++;
                }
                continue;
            }

            i++;
        }

        return new ClusterRecord(name, purpose, phase, nodes);
    }

    private static NodeRecord? ParseNodeFlowMapping(string line)
    {
        var open = line.IndexOf('{');
        var close = line.LastIndexOf('}');
        if (open < 0 || close < 0 || close <= open)
            return null;

        var body = line.Substring(open + 1, close - open - 1);
        string name = "", os = "", vmnet10 = "", vmnet11 = "", dir = "", role = "";

        foreach (var rawPair in SplitTopLevel(body, ','))
        {
            var pair = rawPair.Trim();
            if (pair.Length == 0)
                continue;
            var colon = pair.IndexOf(':');
            if (colon < 0)
                continue;
            var key = pair[..colon].Trim();
            var value = StripValue(pair[(colon + 1)..]);
            switch (key)
            {
                case "name": name = value; break;
                case "os": os = value; break;
                case "vmnet10": vmnet10 = value; break;
                case "vmnet11": vmnet11 = value; break;
                case "dir": dir = value; break;
                case "role": role = value; break;
            }
        }

        return string.IsNullOrEmpty(name) ? null : new NodeRecord(name, os, vmnet10, vmnet11, dir, role);
    }

    private static IEnumerable<string> SplitTopLevel(string s, char sep)
    {
        var depth = 0;
        var inQuote = false;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"')
            {
                inQuote = !inQuote;
                continue;
            }
            if (inQuote)
                continue;
            if (c == '{' || c == '[')
                depth++;
            else if (c == '}' || c == ']')
                depth--;
            else if (c == sep && depth == 0)
            {
                yield return s[start..i];
                start = i + 1;
            }
        }
        if (start <= s.Length)
            yield return s[start..];
    }

    private static string StripValue(string raw)
    {
        var v = raw.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
            return v[1..^1];
        return v;
    }

    private static bool IsComment(string line)
        => line.TrimStart().StartsWith('#');

    private static int Indent(string line)
    {
        var n = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == ' ')
                n++;
            else if (line[i] == '\t')
                n += 2;
            else
                break;
        }
        return n;
    }
}
