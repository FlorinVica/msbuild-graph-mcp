using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>
/// Real-world ProjectGraph failures from dotnet/msbuild GitHub issues:
/// #8574, #7432, #7856, #9502, #8172, #13153.
/// </summary>
public class RealWorldGraphTests
{
    private static GraphAnalysis ParseGraph(string json) =>
        JsonSerializer.Deserialize<GraphAnalysis>(json, Helpers.JsonOptions)!;

    // --- #8574: Performance — 15 projects should build graph in <5s ---

    [Fact]
    public async Task FifteenProjects_GraphConstructionUnder5s()
    {
        using var builder = new TempSolutionBuilder();
        // Build a fan-out: 14 libs, 1 app that references them all
        for (int i = 0; i < 14; i++)
            builder.AddProject($"Lib{i:D2}");
        builder.AddProject("App", outputType: "Exe",
            references: Enumerable.Range(0, 14).Select(i => $"Lib{i:D2}").ToArray());
        var slnx = builder.BuildSlnx();

        var result = ParseGraph(await ProjectGraphTools.GetProjectGraph(slnx));

        Assert.Equal(15, result.Metrics.UniqueProjectCount);
        Assert.True(result.Metrics.ConstructionTimeMs < 5000,
            $"Graph construction took {result.Metrics.ConstructionTimeMs}ms, expected <5000ms");
    }

    // --- #7856: Both TargetFramework AND TargetFrameworks defined ---

    [Fact]
    public async Task BothTfAndTfs_GraphDoesNotCrash()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Normal", tfm: "net8.0");
        builder.BuildSlnx();

        // Create project with BOTH properties (real-world mistake)
        var bothDir = Path.Combine(builder.RootDir, "Both");
        Directory.CreateDirectory(bothDir);
        File.WriteAllText(Path.Combine(bothDir, "Both.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <TargetFrameworks>net8.0;net6.0</TargetFrameworks>
  </PropertyGroup>
</Project>");
        File.WriteAllText(Path.Combine(bothDir, "C.cs"), "namespace Both; public class C {}");

        var slnx = Path.Combine(builder.RootDir, "Test.slnx");
        File.WriteAllText(slnx, @"<Solution>
  <Project Path=""Normal\Normal.csproj"" />
  <Project Path=""Both\Both.csproj"" />
</Solution>");

        // Should not throw InternalErrorException
        var result = ParseGraph(await ProjectGraphTools.GetProjectGraph(slnx));

        Assert.NotNull(result);
        Assert.True(result.Metrics.UniqueProjectCount >= 2);
    }

    // --- #9502: Empty project in graph ---

    [Fact]
    public async Task EmptyProject_ReportsErrorGracefully()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Good");
        builder.BuildSlnx();

        // Create truly empty project
        var emptyDir = Path.Combine(builder.RootDir, "Empty");
        Directory.CreateDirectory(emptyDir);
        File.WriteAllText(Path.Combine(emptyDir, "Empty.csproj"), "<Project></Project>");

        var slnx = Path.Combine(builder.RootDir, "Test.slnx");
        File.WriteAllText(slnx, @"<Solution>
  <Project Path=""Good\Good.csproj"" />
  <Project Path=""Empty\Empty.csproj"" />
</Solution>");

        var result = ParseGraph(await ProjectGraphTools.GetProjectGraph(slnx));

        // Empty project may work (it's valid MSBuild) or may error — either is fine, no crash
        Assert.NotNull(result);
    }

    // --- #8172: Import with Condition="false" to nonexistent file ---

    [Fact]
    public async Task FalseConditionImport_NoError()
    {
        using var builder = new TempSolutionBuilder();
        builder.BuildSlnx();

        var projDir = Path.Combine(builder.RootDir, "Conditional");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "Conditional.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <Import Condition=""false"" Project=""$(MSBuildExtensionsPath)\NonExistent\Fake.targets"" />
</Project>");
        File.WriteAllText(Path.Combine(projDir, "C.cs"), "namespace Conditional; public class C {}");

        // Should evaluate successfully — the import condition is false
        var json = PropertyTools.AnalyzeProjectProperties(
            Path.Combine(projDir, "Conditional.csproj"),
            properties: new[] { "TargetFramework" });

        var result = JsonSerializer.Deserialize<PropertyAnalysis>(json, Helpers.JsonOptions)!;
        Assert.Equal("net8.0", result.Properties[0].EvaluatedValue);
    }

    // --- Chain of 5 projects — verify depth calculation ---

    [Fact]
    public async Task DeepChain_DepthEqualsChainLength()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("L0");
        for (int i = 1; i <= 4; i++)
            builder.AddProject($"L{i}", references: $"L{i - 1}");
        builder.AddProject("Top", outputType: "Exe", references: "L4");
        var slnx = builder.BuildSlnx();

        var result = ParseGraph(await ProjectGraphTools.GetProjectGraph(slnx));

        Assert.Equal(6, result.Metrics.UniqueProjectCount);
        Assert.Equal(5, result.Metrics.MaxDepth); // L0→L1→L2→L3→L4→Top
    }

    // --- Wide fan-out — many projects referencing same base ---

    [Fact]
    public async Task WideFanOut_CorrectReferencedByCount()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Base");
        for (int i = 0; i < 8; i++)
            builder.AddProject($"Consumer{i}", outputType: "Exe", references: "Base");
        var slnx = builder.BuildSlnx();

        var result = ParseGraph(await ProjectGraphTools.GetProjectGraph(slnx));

        var baseNode = result.Projects.First(p => p.Name == "Base");
        Assert.Equal(8, baseNode.ReferencedBy.Count);
        Assert.True(baseNode.IsLeaf);
        Assert.False(baseNode.IsRoot);
    }
}
