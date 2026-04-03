using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>Tests for maxProjects output truncation on get_project_graph.</summary>
public class TruncationTests
{
    private static GraphAnalysis Parse(string json) =>
        JsonSerializer.Deserialize<GraphAnalysis>(json, Helpers.JsonOptions)!;

    [Fact]
    public async Task Truncation_UnderLimit_NotTruncated()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B");
        var slnx = builder.BuildSlnx();

        var result = Parse(await ProjectGraphTools.GetProjectGraph(slnx, maxProjects: 10));

        Assert.Null(result.Truncated);
        Assert.Equal(2, result.Projects.Count);
    }

    [Fact]
    public async Task Truncation_AtLimit_NotTruncated()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B").AddProject("C");
        var slnx = builder.BuildSlnx();

        var result = Parse(await ProjectGraphTools.GetProjectGraph(slnx, maxProjects: 3));

        Assert.Null(result.Truncated);
        Assert.Equal(3, result.Projects.Count);
    }

    [Fact]
    public async Task Truncation_OverLimit_Truncated()
    {
        using var builder = new TempSolutionBuilder();
        for (int i = 0; i < 5; i++)
            builder.AddProject($"P{i}");
        var slnx = builder.BuildSlnx();

        var result = Parse(await ProjectGraphTools.GetProjectGraph(slnx, maxProjects: 3));

        Assert.True(result.Truncated);
        Assert.Equal(3, result.Projects.Count);
        // UniqueProjectCount reflects shown projects (after truncation)
        // NodeCount from MSBuild reflects the full graph
        Assert.True(result.Metrics.NodeCount >= 5);
    }

    [Fact]
    public async Task Truncation_DefaultLimit_Reasonable()
    {
        using var builder = new TempSolutionBuilder();
        for (int i = 0; i < 10; i++)
            builder.AddProject($"P{i}");
        var slnx = builder.BuildSlnx();

        // Default maxProjects=200 — 10 projects should NOT be truncated
        var result = Parse(await ProjectGraphTools.GetProjectGraph(slnx));

        Assert.Null(result.Truncated);
        Assert.Equal(10, result.Projects.Count);
    }
}
