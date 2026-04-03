using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.Runtime.CompilerServices;

// MSBuildLocator MUST be called before any Microsoft.Build type is JIT-compiled.
// Even a method signature referencing an MSBuild type triggers assembly loading.
// That's why RunMcpServer() is in a separate method with [NoInlining].

// SECURITY: Block arbitrary code execution via MSBuild property functions (CVE-2025-21172)
var dangerousEnvVar = Environment.GetEnvironmentVariable("MSBUILDENABLEALLPROPERTYFUNCTIONS");
if (dangerousEnvVar != null && dangerousEnvVar != "0")
{
    Console.Error.WriteLine("SECURITY: MSBUILDENABLEALLPROPERTYFUNCTIONS is set — this enables arbitrary code execution during MSBuild evaluation. Refusing to start.");
    Environment.Exit(2);
}

bool registered = false;

// Try explicit query with all discovery types
var instances = MSBuildLocator.QueryVisualStudioInstances(
        new VisualStudioInstanceQueryOptions
        {
            DiscoveryTypes = DiscoveryType.DotNetSdk | DiscoveryType.VisualStudioSetup | DiscoveryType.DeveloperConsole
        })
    .OrderByDescending(i => i.Version)
    .ToList();

if (instances.Count > 0)
{
    var chosen = instances.First();
    Console.Error.WriteLine($"MSBuild: {chosen.MSBuildPath} (v{chosen.Version}, {chosen.DiscoveryType})");
    MSBuildLocator.RegisterInstance(chosen);
    registered = true;
}

// Fallback: RegisterDefaults uses additional heuristics
if (!registered)
{
    try
    {
        MSBuildLocator.RegisterDefaults();
        Console.Error.WriteLine("MSBuild: registered via defaults");
        registered = true;
    }
    catch (InvalidOperationException)
    {
        // ignored — will fail below
    }
}

if (!registered)
{
    Console.Error.WriteLine("ERROR: No Visual Studio or .NET SDK installation found.");
    Console.Error.WriteLine("Install Visual Studio 2022+ or .NET SDK 8.0+.");
    Environment.Exit(1);
}

await RunMcpServer(args);

[MethodImpl(MethodImplOptions.NoInlining)]
static async Task RunMcpServer(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    // All logging to stderr — stdout is reserved for MCP JSON-RPC
    builder.Logging.AddConsole(opts =>
        opts.LogToStandardErrorThreshold = LogLevel.Trace);

    builder.Services
        .AddMcpServer(options =>
        {
            options.ServerInfo = new()
            {
                Name = "msbuild-graph-mcp",
                Version = "1.1.1"
            };
        })
        .WithStdioServerTransport()
        .WithToolsFromAssembly()
        .WithPromptsFromAssembly();

    await builder.Build().RunAsync();
}
