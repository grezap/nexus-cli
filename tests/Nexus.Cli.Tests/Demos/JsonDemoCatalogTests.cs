using FluentAssertions;
using Nexus.Cli.Adapters.Demos;
using Xunit;

namespace Nexus.Cli.Tests.Demos;

/// <summary>
/// Covers ADR-0009 (extended System B JSON demo spec): minimal v0.4.0 specs
/// must keep working, fully-extended specs must populate all new optional
/// fields, and partial specs must default the absent fields to null/empty.
/// </summary>
public class JsonDemoCatalogTests : IDisposable
{
    private readonly string _tempDir;

    public JsonDemoCatalogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nexus-cli-test-demos-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Load_MinimalV040Spec_ParsesUnchanged()
    {
        const string json = """
        {
          "id": "DEMO-T-01-minimal",
          "title": "Minimal v0.4 spec",
          "description": "Backwards-compatible spec without any v0.6 fields.",
          "steps": [
            { "command": "echo hello", "waitAfterSeconds": 1 }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(_tempDir, "DEMO-T-01.json"), json);

        var catalog = new JsonDemoCatalog(_tempDir);
        var loaded = catalog.Load();

        loaded.IsOk.Should().BeTrue();
        loaded.Value.Should().ContainKey("DEMO-T-01-minimal");
        var spec = loaded.Value!["DEMO-T-01-minimal"];
        spec.Id.Should().Be("DEMO-T-01-minimal");
        spec.Title.Should().Be("Minimal v0.4 spec");
        spec.Steps.Should().ContainSingle();
        spec.Steps[0].Command.Should().Be("echo hello");
        spec.Steps[0].WaitAfterSeconds.Should().Be(1);
        // v0.6 fields default to null when absent
        spec.Steps[0].ExpectedExitCode.Should().BeNull();
        spec.Steps[0].ExpectedOutputContains.Should().BeNull();
        spec.Steps[0].Observations.Should().BeNull();
        spec.Prerequisites.Should().BeNull();
        spec.WhatProves.Should().BeNull();
    }

    [Fact]
    public void Load_FullyExtendedSpec_ParsesAllNewFields()
    {
        const string json = """
        {
          "id": "DEMO-T-02-extended",
          "title": "Extended spec",
          "description": "All v0.6 fields populated.",
          "prerequisites": {
            "vmsAlive": ["redis-1", "redis-2"],
            "envVars": ["NEXUS_SSH_KEY"]
          },
          "steps": [
            {
              "command": "nexus failover-test redis-shard --shard 1",
              "waitAfterSeconds": 3,
              "expectedExitCode": 0,
              "expectedOutputContains": ["new leader elected", "RTO:"],
              "observe": [
                { "where": "stdout", "what": "RTO seconds under 5" },
                { "where": "Grafana panel redis-cluster-overview", "what": "primary indicator flips" }
              ]
            }
          ],
          "whatProves": "Redis Cluster auto-failover under 5s."
        }
        """;
        File.WriteAllText(Path.Combine(_tempDir, "DEMO-T-02.json"), json);

        var catalog = new JsonDemoCatalog(_tempDir);
        var loaded = catalog.Load();

        loaded.IsOk.Should().BeTrue();
        var spec = loaded.Value!["DEMO-T-02-extended"];
        spec.Prerequisites.Should().NotBeNull();
        spec.Prerequisites!.VmsAlive.Should().BeEquivalentTo(new[] { "redis-1", "redis-2" });
        spec.Prerequisites.EnvVars.Should().BeEquivalentTo(new[] { "NEXUS_SSH_KEY" });
        spec.WhatProves.Should().Be("Redis Cluster auto-failover under 5s.");
        var step = spec.Steps.Single();
        step.ExpectedExitCode.Should().Be(0);
        step.ExpectedOutputContains.Should().BeEquivalentTo(new[] { "new leader elected", "RTO:" });
        step.Observations.Should().HaveCount(2);
        step.Observations![0].Where.Should().Be("stdout");
        step.Observations[0].What.Should().Be("RTO seconds under 5");
        step.Observations[1].Where.Should().Be("Grafana panel redis-cluster-overview");
    }

    [Fact]
    public void Load_PartiallyExtendedSpec_DefaultsAbsentFields()
    {
        const string json = """
        {
          "id": "DEMO-T-03-partial",
          "title": "Partial spec",
          "description": "Only expectedExitCode set on one step.",
          "steps": [
            {
              "command": "echo hello",
              "waitAfterSeconds": 1,
              "expectedExitCode": 0
            }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(_tempDir, "DEMO-T-03.json"), json);

        var catalog = new JsonDemoCatalog(_tempDir);
        var loaded = catalog.Load();

        loaded.IsOk.Should().BeTrue();
        var spec = loaded.Value!["DEMO-T-03-partial"];
        spec.Prerequisites.Should().BeNull();
        spec.WhatProves.Should().BeNull();
        var step = spec.Steps.Single();
        step.ExpectedExitCode.Should().Be(0);
        step.ExpectedOutputContains.Should().BeNull();
        step.Observations.Should().BeNull();
    }

    [Fact]
    public void Load_EmptyArraysInExtendedFields_TreatedAsAbsent()
    {
        // Empty `expectedOutputContains: []` and `observe: []` should map to null
        // (the catalog uses `is { Count: > 0 }` to discriminate). Keeps demo specs
        // tidy -- an empty array means "no expectation" rather than "expect nothing".
        const string json = """
        {
          "id": "DEMO-T-04-empty-arrays",
          "title": "Empty extended arrays",
          "description": "Empty arrays should not populate the model.",
          "steps": [
            {
              "command": "echo hello",
              "waitAfterSeconds": 1,
              "expectedOutputContains": [],
              "observe": []
            }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(_tempDir, "DEMO-T-04.json"), json);

        var catalog = new JsonDemoCatalog(_tempDir);
        var loaded = catalog.Load();

        loaded.IsOk.Should().BeTrue();
        var step = loaded.Value!["DEMO-T-04-empty-arrays"].Steps.Single();
        step.ExpectedOutputContains.Should().BeNull();
        step.Observations.Should().BeNull();
    }
}
