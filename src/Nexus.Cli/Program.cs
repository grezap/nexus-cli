// Step-2 placeholder. Spectre.Console.Cli wiring + ClusterStatusCommand land in Step 3.
using System;

const string Version = "0.1.0-alpha.1";

if (args.Length == 1 && (args[0] == "--version" || args[0] == "-v"))
{
    Console.WriteLine(Version);
    return 0;
}

Console.WriteLine($"nexus {Version} — vertical-slice scaffold; commands land in step 3.");
return 0;
