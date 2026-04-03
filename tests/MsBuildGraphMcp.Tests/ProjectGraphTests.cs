using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

public class ProjectGraphTests
{
    private static GraphAnalysis Parse(string json) =>
        JsonSerializer.Deserialize<GraphAnalysis>(json, Helpers.JsonOptions)!;

    // --- Basic graph construction ---

    [Fact]
    public async Task SingleProject_OneNode_ZeroEdges()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lonely");
        var slnx = builder.BuildSlnx();

        var result = Parse(await ProjectGraphTools.GetProjectGraph(slnx));

        Assert.Equal(1, result.Metrics.NodeCount);
        Assert.Equal(0, result.Metrics.EdgeCount);
        Assert.Single(result.Projects);
        Assert.True(result.Projects[0].IsRoot);
        Assert.True(result.Projects[0].IsLeaf);
    }

    [Fact]
    public async Task LinearChain_CorrectDepthAndOrder()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A")
               .AddProject("B", references: "A")
               .AddProject("C", outputType: "Exe", references: "B");
        var slnx = builder.BuildSlnx();

        var result = Parse(await ProjectGraphTools.GetProjectGraph(slnx));

        Assert.Equal(3, result.Metrics.UniqueProjectCount);
        Assert.True(result.Metrics.EdgeCount >= 2); // at least A→B, B→C
        Assert.Equal(2, result.Metrics.MaxDepth);

        // Topological: leaf (A) first, root (C) last
        var order = result.TopologicalOrder.Select(Path.GetFileNameWithoutExtension).ToList();
        Assert.True(order.IndexOf("A") < order.IndexOf("B"));
        Assert.True(order.IndexOf("B") < order.IndexOf("C"));
    }

    [Fact]
    public async Task DiamondDependency_FourNodes_CorrectTopology()
    {
        // App → Lib1, App → Lib2, Lib1 → Core, Lib2 → Core
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Core")
               .AddProject("Lib1", references: "Core")
               .AddProject("Lib2", references: "Core")
               .AddProject("App", outputType: "Exe", references: new[] { "Lib1", "Lib2" });
        var slnx = builder.BuildSlnx();

        var result = Parse(await ProjectGraphTools.GetProjectGraph(slnx));

        Assert.Equal(4, result.Metrics.UniqueProjectCount);
        Assert.True(result.Metrics.EdgeCount >= 4); // at least the 4 direct edges
        Assert.Equal(2, result.Metrics.MaxDepth);

        var core = result.Projects.First(p => p.Name == "Core");
        Assert.True(core.IsLeaf);
        Assert.True(core.ReferencedBy.Count >= 2); // Lib1 + Lib2

        var app = result.Projects.First(p => p.Name == "App");
        Assert.True(app.IsRoot);
        Assert.True(app.References.Count >= 2);
    }

    // --- .sln vs .slnx vs .csproj entry points ---

    [Fact]
    public async Task SlnxEntryPoint_TwoProjects_Works()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib").AddProject("App", outputType: "Exe", references: "Lib");
        var slnx = builder.BuildSlnx();

        var result = Parse(await ProjectGraphTools.GetProjectGraph(slnx));

        Assert.Equal(2, result.Metrics.UniqueProjectCount);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task CsprojEntryPoint_Works()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Core").AddProject("App", outputType: "Exe", references: "Core");
        builder.BuildSln(); // writes the files

        var result = Parse(await ProjectGraphTools.GetProjectGraph(builder.GetProjectPath("App")));

        Assert.Equal(2, result.Metrics.UniqueProjectCount);
    }

    // --- Configuration parameter ---

    [Fact]
    public async Task ConfigurationParameter_PassedToGraph()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var slnx = builder.BuildSlnx();

        var result = Parse(await ProjectGraphTools.GetProjectGraph(slnx, configuration: "Release"));

        Assert.Equal(1, result.Metrics.UniqueProjectCount);
        Assert.Empty(result.Errors);
    }

    // --- Isolated project (no deps) ---

    [Fact]
    public async Task OrphanInGraph_ShowsAsRootAndLeaf()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Connected").AddProject("App", outputType: "Exe", references: "Connected")
               .AddProject("Orphan");
        var slnx = builder.BuildSlnx();

        var result = Parse(await ProjectGraphTools.GetProjectGraph(slnx));

        var orphan = result.Projects.First(p => p.Name == "Orphan");
        Assert.True(orphan.IsRoot);
        Assert.True(orphan.IsLeaf);
        Assert.Empty(orphan.References);
        Assert.Empty(orphan.ReferencedBy);
    }

    // --- Metrics ---

    [Fact]
    public async Task ConstructionTime_IsPositive()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A");
        var slnx = builder.BuildSlnx();

        var result = Parse(await ProjectGraphTools.GetProjectGraph(slnx));

        Assert.True(result.Metrics.ConstructionTimeMs > 0);
    }

    // --- Error handling ---

    [Fact]
    public async Task InvalidPath_ReturnsError()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => ProjectGraphTools.GetProjectGraph(@"C:\nonexistent\foo.sln"));
    }

    // --- Output type detection ---

    [Fact]
    public async Task OutputType_DetectedCorrectly()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib", outputType: "Library")
               .AddProject("App", outputType: "Exe");
        var slnx = builder.BuildSlnx();

        var result = Parse(await ProjectGraphTools.GetProjectGraph(slnx));

        Assert.Contains(result.Projects, p => p.Name == "Lib" && p.OutputType == "Library");
        Assert.Contains(result.Projects, p => p.Name == "App" && p.OutputType == "Exe");
    }

    // --- Empty graph ---

    [Fact]
    public async Task EmptySolution_ZeroNodes()
    {
        using var builder = new TempSolutionBuilder();
        var slnx = builder.BuildSlnx();

        var result = Parse(await ProjectGraphTools.GetProjectGraph(slnx));

        Assert.Equal(0, result.Metrics.NodeCount);
        Assert.Empty(result.Projects);
    }
}
