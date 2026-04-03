using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>Edge cases for get_build_order: empty, deep, config, wide fan-out.</summary>
public class BuildOrderEdgeCaseTests
{
    private static BuildOrderAnalysis Parse(string json) =>
        JsonSerializer.Deserialize<BuildOrderAnalysis>(json, Helpers.JsonOptions)!;

    [Fact]
    public async Task BuildOrder_EmptySolution_ZeroProjects()
    {
        using var builder = new TempSolutionBuilder();
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildOrderTools.GetBuildOrder(slnx));

        Assert.Equal(0, result.TotalProjects);
        Assert.Equal(0, result.CriticalPathLength);
        Assert.Empty(result.BuildOrder);
    }

    [Fact]
    public async Task BuildOrder_DeepChain_CorrectCriticalPath()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("L0");
        for (int i = 1; i <= 5; i++)
            builder.AddProject($"L{i}", references: $"L{i - 1}");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildOrderTools.GetBuildOrder(slnx));

        Assert.Equal(6, result.TotalProjects);
        Assert.Equal(5, result.CriticalPathLength);
        Assert.Equal("L0", result.BuildOrder[0]);
        Assert.Equal("L5", result.BuildOrder[^1]);
    }

    [Fact]
    public async Task BuildOrder_WideFanOut_DepthOne()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Base");
        for (int i = 0; i < 5; i++)
            builder.AddProject($"Consumer{i}", outputType: "Exe", references: "Base");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildOrderTools.GetBuildOrder(slnx));

        Assert.Equal(6, result.TotalProjects);
        Assert.Equal(1, result.CriticalPathLength);
        Assert.Equal("Base", result.BuildOrder[0]);
    }

    [Fact]
    public async Task BuildOrder_WithConfiguration_NoError()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildOrderTools.GetBuildOrder(slnx, configuration: "Release", platform: "AnyCPU"));

        Assert.Equal(1, result.TotalProjects);
        Assert.Empty(result.Errors);
        Assert.Null(result.CircularDependencyError);
    }

    [Fact]
    public async Task BuildOrder_InvalidPath_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => BuildOrderTools.GetBuildOrder(@"C:\nonexistent.sln"));
    }

    [Fact]
    public async Task BuildOrder_OutputNamesNotPaths()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("MyLib");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildOrderTools.GetBuildOrder(slnx));

        // Should be just names, not full paths
        Assert.Equal("MyLib", result.BuildOrder[0]);
        Assert.DoesNotContain("\\", result.BuildOrder[0]);
        Assert.DoesNotContain(".csproj", result.BuildOrder[0]);
    }
}
