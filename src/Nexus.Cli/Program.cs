using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Cli.Commands;
using Nexus.Cli.Commands.Demo;
using Nexus.Cli.Commands.FailoverTest;
using Nexus.Cli.Commands.Infrastructure;
using Nexus.Cli.Commands.KafkaFailover;
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
            config.SetApplicationVersion("0.5.0");

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
                failover.AddCommand<FailoverTestNomadLeaderCommand>("nomad-leader")
                    .WithDescription("Stop the current Nomad leader; measure new-leader election RTO; auto-recover.")
                    .WithExample(["failover-test", "nomad-leader"])
                    .WithExample(["failover-test", "nomad-leader", "--json"])
                    .WithExample(["failover-test", "nomad-leader", "--yes"]);
                failover.AddCommand<FailoverTestSwarmManagerCommand>("swarm-manager")
                    .WithDescription("Vmrun-suspend the current Docker Swarm raft leader VM (HOST-LEVEL outage); measure RTO; vmrun-resume to recover.")
                    .WithExample(["failover-test", "swarm-manager"])
                    .WithExample(["failover-test", "swarm-manager", "--json"])
                    .WithExample(["failover-test", "swarm-manager", "--yes"]);
            });

            config.AddBranch("kafka", kafka =>
            {
                kafka.SetDescription("Kafka DR helpers (Phase 0.H Kafka ecosystem must be live).");
                kafka.AddBranch("failover", failover =>
                {
                    failover.SetDescription("Drive a region-loss DR failover between the East + West KRaft clusters and measure RTO. See ADR-0008 for the v0.5.0 demo-grade scope.");
                    failover.AddCommand<KafkaFailoverEastToWestCommand>("east-to-west")
                        .WithDescription("Vmrun-suspend the 3 kafka-east brokers (HOST-LEVEL region-loss simulation); verify kafka-west keeps serving via an RF=3 produce/consume round-trip; measure RTO; vmrun-resume to recover.")
                        .WithExample(["kafka", "failover", "east-to-west"])
                        .WithExample(["kafka", "failover", "east-to-west", "--json"])
                        .WithExample(["kafka", "failover", "east-to-west", "--yes"]);
                    failover.AddCommand<KafkaFailoverWestToEastCommand>("west-to-east")
                        .WithDescription("Vmrun-suspend the 3 kafka-west brokers (HOST-LEVEL region-loss simulation); verify kafka-east keeps serving via an RF=3 produce/consume round-trip; measure RTO; vmrun-resume to recover.")
                        .WithExample(["kafka", "failover", "west-to-east"])
                        .WithExample(["kafka", "failover", "west-to-east", "--json"])
                        .WithExample(["kafka", "failover", "west-to-east", "--yes"]);
                });
            });

            config.AddBranch("demo", demo =>
            {
                demo.SetDescription("Demo orchestrator + recorder.");
                demo.AddCommand<DemoListCommand>("list")
                    .WithDescription("List demos available in the catalog.")
                    .WithExample(["demo", "list"]);
                demo.AddCommand<DemoRunCommand>("run")
                    .WithDescription("Run a demo by id; execute its steps sequentially.")
                    .WithExample(["demo", "run", "DEMO-01-cluster-status"])
                    .WithExample(["demo", "run", "DEMO-01-cluster-status", "--json"]);
                demo.AddCommand<DemoRecordCommand>("record")
                    .WithDescription("Generate a VHS .tape from the demo and render to GIF via vhs.")
                    .WithExample(["demo", "record", "DEMO-01-cluster-status"])
                    .WithExample(["demo", "record", "DEMO-01-cluster-status", "--output-dir", "./out"]);
            });
        });

        return await app.RunAsync(args).ConfigureAwait(false);
    }
}
