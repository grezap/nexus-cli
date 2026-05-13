using System.Text.Json;
using Nexus.Cli.Adapters.Json;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Demos;

/// <summary>
/// Loads demo specs from a directory of <c>&lt;id&gt;.json</c> files. Source-gen
/// JSON via <see cref="NexusJsonContext"/>; no reflection, AOT-clean.
///
/// Path discovery: explicit ctor arg → <see cref="DemosDirEnvVar"/> env →
/// sibling fallback (<c>../docs/demos/</c> from cwd, then the repo's own
/// <c>docs/demos/</c>).
/// </summary>
public sealed class JsonDemoCatalog : IDemoCatalog
{
    public const string DemosDirEnvVar = "NEXUS_DEMOS_PATH";

    private readonly string? _explicitDir;
    private IReadOnlyDictionary<string, DemoSpec>? _cache;
    private string? _loadError;

    public JsonDemoCatalog(string? explicitDir = null) => _explicitDir = explicitDir;

    public Result<IReadOnlyDictionary<string, DemoSpec>> Load()
    {
        if (_cache is not null)
            return Result.Ok(_cache);
        if (_loadError is not null)
            return Result.Fail<IReadOnlyDictionary<string, DemoSpec>>(_loadError);

        var dir = ResolveDir();
        if (dir is null)
        {
            _loadError = $"demos directory not found. Set {DemosDirEnvVar} to a directory of <id>.json files, or place them under ./docs/demos/.";
            return Result.Fail<IReadOnlyDictionary<string, DemoSpec>>(_loadError);
        }

        try
        {
            var result = new Dictionary<string, DemoSpec>(StringComparer.OrdinalIgnoreCase);
            foreach (var jsonFile in Directory.EnumerateFiles(dir, "*.json"))
            {
                var content = File.ReadAllText(jsonFile);
                var dto = JsonSerializer.Deserialize(content, NexusJsonContext.Default.DemoSpecJson);
                if (dto is null)
                    continue;
                if (string.IsNullOrEmpty(dto.Id))
                    continue;
                var steps = (dto.Steps ?? new List<DemoStepJson>())
                    .Select(s => new DemoStep(s.Command ?? string.Empty, s.WaitAfterSeconds))
                    .ToList();
                result[dto.Id] = new DemoSpec(
                    dto.Id,
                    dto.Title ?? dto.Id,
                    dto.Description ?? string.Empty,
                    steps);
            }
            _cache = result;
            return Result.Ok(_cache);
        }
        catch (Exception ex)
        {
            _loadError = $"failed to read demos directory '{dir}': {ex.Message}";
            return Result.Fail<IReadOnlyDictionary<string, DemoSpec>>(_loadError);
        }
    }

    public Result<DemoSpec> GetDemo(string id)
    {
        var loaded = Load();
        if (loaded.IsFail)
            return Result.Fail<DemoSpec>(loaded.Error!);
        if (loaded.Value!.TryGetValue(id, out var spec))
            return Result.Ok(spec);
        var known = string.Join(", ", loaded.Value.Keys.OrderBy(k => k, StringComparer.Ordinal));
        return Result.Fail<DemoSpec>(
            string.IsNullOrEmpty(known)
                ? $"no demos found in catalog; check {DemosDirEnvVar} or ./docs/demos/"
                : $"unknown demo '{id}'. Known: {known}");
    }

    private string? ResolveDir()
    {
        if (!string.IsNullOrWhiteSpace(_explicitDir) && Directory.Exists(_explicitDir))
            return _explicitDir;
        var env = Environment.GetEnvironmentVariable(DemosDirEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;
        // Cwd-relative ./docs/demos/.
        var cwdCandidate = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "docs", "demos"));
        if (Directory.Exists(cwdCandidate))
            return cwdCandidate;
        // Parent-cwd ../docs/demos/ (running from artifacts/<rid>/).
        var parentCandidate = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "docs", "demos"));
        if (Directory.Exists(parentCandidate))
            return parentCandidate;
        return null;
    }
}
