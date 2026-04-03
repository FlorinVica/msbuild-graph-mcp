using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>Tests for multi-targeting, circular deps, cancellation, and edge cases in get_project_graph.</summary>
public class ProjectGraphEdgeCaseTests
{
    private static GraphAnalysis Parse(string json) =>
        JsonSerializer.Deserialize<GraphAnalysis>(json, Helpers.JsonOptions)!;

    // --- Multi-targeting deduplication ---

    [Fact]
    public async Task MultiTargetProject_DeduplicatedToSingleNode()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddMultiTargetProject("SharedLib", "net8.0;net6.0")
               .AddProject("App", tfm: "net8.0", outputType: "Exe", references: "SharedLib");
        var slnx = builder.BuildSlnx();

        var result = Parse(await ProjectGraphTools.GetProjectGraph(slnx));

        // SharedLib has 2 inner builds + 1 outer = 3 graph nodes, but should deduplicate to 1
        var sharedLib = result.Projects.FirstOrDefault(p => p.Name == "SharedLib");
        Assert.NotNull(sharedLib);
        Assert.True(sharedLib.IsMultiTargeting);
        Assert.Contains("net8.0", sharedLib.TargetFrameworks);
        Assert.Contains("net6.0", sharedLib.TargetFrameworks);
        Assert.Equal(2, result.Metrics.UniqueProjectCount); // SharedLib + App
    }

    [Fact]
    public async Task MultiTargetProject_ThreeTfms_AllListed()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddMultiTargetProject("Lib", "net8.0;net6.0;netstandard2.0");
        var slnx = builder.BuildSlnx();

        var result = Parse(await ProjectGraphTools.GetProjectGraph(slnx));

        var lib = result.Projects.First(p => p.Name == "Lib");
        Assert.True(lib.IsMultiTargeting);
        Assert.Equal(3, lib.TargetFrameworks.Count);
    }

    [Fact]
    public async Task MixedSingleAndMultiTarget_CorrectCounts()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddMultiTargetProject("Multi", "net8.0;net6.0")
               .AddProject("Single", tfm: "net8.0")
               .AddProject("App", tfm: "net8.0", outputType: "Exe", references: new[] { "Multi", "Single" });
        var slnx = builder.BuildSlnx();

        var result = Parse(await ProjectGraphTools.GetProjectGraph(slnx));

        Assert.Equal(3, result.Metrics.UniqueProjectCount);
        Assert.True(result.Projects.First(p => p.Name == "Multi").IsMultiTargeting);
        Assert.False(result.Projects.First(p => p.Name == "Single").IsMultiTargeting);
    }

    // --- Circular dependency handling ---

    [Fact]
    public async Task CircularDependency_ReturnsErrorInResult()
    {
        using var builder = new TempSolutionBuilder();

        // Create two projects that reference each other: A→B and B→A
        var dirA = Path.Combine(builder.RootDir, "A");
        var dirB = Path.Combine(builder.RootDir, "B");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        File.WriteAllText(Path.Combine(dirA, "A.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <ItemGroup><ProjectReference Include=""..\B\B.csproj"" /></ItemGroup>
</Project>");
        File.WriteAllText(Path.Combine(dirA, "C.cs"), "namespace A; public class C {}");

        File.WriteAllText(Path.Combine(dirB, "B.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <ItemGroup><ProjectReference Include=""..\A\A.csproj"" /></ItemGroup>
</Project>");
        File.WriteAllText(Path.Combine(dirB, "C.cs"), "namespace B; public class C {}");

        var result = Parse(await ProjectGraphTools.GetProjectGraph(Path.Combine(dirA, "A.csproj")));

        // ProjectGraph throws on circular deps — should be caught and reported
        Assert.True(
            result.CircularDependencyError != null || result.Errors.Count > 0,
            "Circular dependency should be reported as error");
    }

    // --- Configuration + Platform together ---

    [Fact]
    public async Task ConfigAndPlatform_BothPropagated()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var slnx = builder.BuildSlnx();

        // Should not throw — config/platform are passed as global properties
        var result = Parse(await ProjectGraphTools.GetProjectGraph(
            slnx, configuration: "Release", platform: "x64"));

        Assert.Equal(1, result.Metrics.UniqueProjectCount);
        Assert.Empty(result.Errors);
    }

    // --- CancellationToken ---

    [Fact]
    public async Task CancellationToken_PreCancelled_ReturnsErrorOrThrows()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var slnx = builder.BuildSlnx();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Tool may either throw or return error JSON (exceptions are caught internally)
        try
        {
            var json = await ProjectGraphTools.GetProjectGraph(slnx, cancellationToken: cts.Token);
            var result = Parse(json);
            Assert.True(result.Errors.Count > 0 || result.Metrics.NodeCount == 0,
                "Pre-cancelled token should result in errors or empty graph");
        }
        catch (OperationCanceledException)
        {
            // Also acceptable
        }
    }

    // --- ConstructionTime always positive ---

    [Fact]
    public async Task LargerSolution_ConstructionTimePositive()
    {
        using var builder = new TempSolutionBuilder();
        for (int i = 0; i < 5; i++)
            builder.AddProject($"Lib{i}");
        builder.AddProject("App", outputType: "Exe",
            references: Enumerable.Range(0, 5).Select(i => $"Lib{i}").ToArray());
        var slnx = builder.BuildSlnx();

        var result = Parse(await ProjectGraphTools.GetProjectGraph(slnx));

        Assert.True(result.Metrics.ConstructionTimeMs > 0);
        Assert.Equal(6, result.Metrics.UniqueProjectCount);
    }

    // --- All nodes are leaf when no deps ---

    [Fact]
    public async Task MultipleIsolatedProjects_AllRootAndLeaf()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B").AddProject("C");
        var slnx = builder.BuildSlnx();

        var result = Parse(await ProjectGraphTools.GetProjectGraph(slnx));

        Assert.All(result.Projects, p =>
        {
            Assert.True(p.IsRoot);
            Assert.True(p.IsLeaf);
            Assert.Empty(p.References);
            Assert.Empty(p.ReferencedBy);
        });
    }
}
