using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Cli.Commands;
using Nexus.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace Nexus.Cli;

internal static class Program
{
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Spectre.Console.Cli's reflection use is bounded; commands are pre-registered via TypeRegistrar.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Spectre.Console.Cli command/setting types are reachable via DI registration.")]
    public static async Task<int> Main(string[] args)
    {
        // Keeps the trimmer from dropping Command/Settings type metadata.
        AotRoots.KeepAlive();

        var services = new ServiceCollection();
        var registrar = new TypeRegistrar(services);

        var app = new CommandApp(registrar);
        app.Configure(config =>
        {
            config.SetApplicationName("nexus");
            config.SetApplicationVersion("0.1.0");

            config.AddCommand<ClusterStatusCommand>("cluster-status")
                .WithDescription("Health of Consul + Nomad + Portainer in the live lab cluster.")
                .WithExample(["cluster-status"])
                .WithExample(["cluster-status", "--json"]);

            config.AddCommand<InfrastructureCommand>("infrastructure")
                .WithDescription("(stub, v0.2) Suspend/resume/status of VM groups.");

            config.AddCommand<FailoverTestCommand>("failover-test")
                .WithDescription("(stub, v0.3) Drive a manager loss + raft re-election; measure RTO.");

            config.AddBranch("kafka", kafka =>
            {
                kafka.SetDescription("(stub, v0.5) Kafka DR helpers.");
                kafka.AddCommand<KafkaFailoverCommand>("failover")
                    .WithDescription("(stub) East→West DR via MM2.");
            });

            config.AddBranch("demo", demo =>
            {
                demo.SetDescription("(stub, v0.4) Demo orchestrator + recorder.");
                demo.AddCommand<DemoRunCommand>("run")
                    .WithDescription("(stub) Run a single demo by id.");
                demo.AddCommand<DemoRecordCommand>("record")
                    .WithDescription("(stub) Record one demo or --all for CI.");
            });
        });

        return await app.RunAsync(args).ConfigureAwait(false);
    }
}
