using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Cli.Commands;
using Nexus.Cli.Commands.FailoverTest;
using Nexus.Cli.Commands.Infrastructure;
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
        // Force UTF-8 on stdout/stderr so Spectre's box-drawing + status glyphs (●, ─, etc.)
        // render correctly on Windows pwsh, which defaults to cp1252 + would emit '?' for
        // anything outside that page. No-op on Linux where stdout is already UTF-8.
        Console.OutputEncoding = Encoding.UTF8;

        // Keeps the trimmer from dropping Command/Settings type metadata.
        AotRoots.KeepAlive();

        var services = new ServiceCollection();
        var registrar = new TypeRegistrar(services);

        var app = new CommandApp(registrar);
        app.Configure(config =>
        {
            config.SetApplicationName("nexus");
            config.SetApplicationVersion("0.3.0");

            config.AddCommand<ClusterStatusCommand>("cluster-status")
                .WithDescription("Health of Consul + Nomad + Portainer in the live lab cluster.")
                .WithExample(["cluster-status"])
                .WithExample(["cluster-status", "--json"]);

            config.AddBranch("infrastructure", infra =>
            {
                infra.SetDescription("Suspend/resume/status of VMware Workstation VM groups defined in vms.yaml.");
                infra.AddCommand<InfrastructureListCommand>("list")
                    .WithDescription("List every VM in vms.yaml decorated with live VMware state.")
                    .WithExample(["infrastructure", "list"])
                    .WithExample(["infrastructure", "list", "--json"]);
                infra.AddCommand<InfrastructureStatusCommand>("status")
                    .WithDescription("Show live state of one cluster (and optionally a single node).")
                    .WithExample(["infrastructure", "status", "foundation"])
                    .WithExample(["infrastructure", "status", "foundation", "--node", "vault-1"]);
                infra.AddCommand<InfrastructureSuspendCommand>("suspend")
                    .WithAlias("suspend-cluster")
                    .WithDescription("Suspend every (or one) running VM in a cluster. Aliased as 'suspend-cluster' per master plan §5.3.")
                    .WithExample(["infrastructure", "suspend", "foundation"])
                    .WithExample(["infrastructure", "suspend", "foundation", "--node", "vault-3", "--yes"])
                    .WithExample(["infrastructure", "suspend-cluster", "foundation", "--yes"]);
                infra.AddCommand<InfrastructureResumeCommand>("resume")
                    .WithDescription("Resume every (or one) stopped/suspended VM in a cluster.")
                    .WithExample(["infrastructure", "resume", "foundation"])
                    .WithExample(["infrastructure", "resume", "foundation", "--node", "vault-3", "--yes"]);
            });

            config.AddBranch("failover-test", failover =>
            {
                failover.SetDescription("Drive a planned failure of a control-plane leader and measure RTO.");
                failover.AddCommand<FailoverTestConsulLeaderCommand>("consul-leader")
                    .WithDescription("Stop the current Consul leader; measure new-leader election RTO; auto-recover.")
                    .WithExample(["failover-test", "consul-leader"])
                    .WithExample(["failover-test", "consul-leader", "--json"])
                    .WithExample(["failover-test", "consul-leader", "--yes"]);
            });

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
