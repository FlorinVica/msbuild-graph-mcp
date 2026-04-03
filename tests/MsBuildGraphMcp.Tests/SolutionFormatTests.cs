using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>
/// .sln/.slnx format edge cases from dotnet/msbuild issues #5027, #10012.
/// Blank lines, unrecognized GUIDs, .slnx on net8.0, parity between formats.
/// </summary>
public class SolutionFormatTests
{
    private static SolutionAnalysis Parse(string json) =>
        JsonSerializer.Deserialize<SolutionAnalysis>(json, Helpers.JsonOptions)!;

    // --- .sln with leading blank lines (#5027) ---

    [Fact]
    public async Task SlnWithLeadingBlankLines_FailsGracefully()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var sln = builder.BuildSln();

        // Prepend blank lines — MSBuild's SolutionFile.Parse rejects this with
        // "No file format header found" (dotnet/msbuild#5027)
        var content = File.ReadAllText(sln);
        File.WriteAllText(sln, "\r\n\r\n" + content);

        // Server should either parse it or throw a clear error
        try
        {
            var result = Parse(await SolutionTools.AnalyzeSolution(sln, CancellationToken.None));
            Assert.Single(result.Projects);
        }
        catch (Microsoft.Build.Exceptions.InvalidProjectFileException ex)
        {
            // Known MSBuild limitation — blank lines before header
            Assert.Contains("header", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- .slnx as entry point to all tools on net8.0 ---

    [Fact]
    public async Task SlnxAnalyzeSolution_WorksOnNet8()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B");
        var slnx = builder.BuildSlnx();

        var result = Parse(await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None));

        Assert.Equal("slnx", result.Format);
        Assert.Equal(2, result.ProjectCount);
    }

    [Fact]
    public async Task SlnxGetProjectGraph_WorksOnNet8()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B", references: "A");
        var slnx = builder.BuildSlnx();

        var json = await ProjectGraphTools.GetProjectGraph(slnx);
        var result = JsonSerializer.Deserialize<GraphAnalysis>(json, Helpers.JsonOptions)!;

        Assert.Equal(2, result.Metrics.UniqueProjectCount);
    }

    [Fact]
    public async Task SlnxFindSharedImports_WorksOnNet8()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B").WithDirectoryBuildProps();
        var slnx = builder.BuildSlnx();

        var json = await SharedImportTools.FindSharedImports(slnx);
        var result = JsonSerializer.Deserialize<SharedImportAnalysis>(json, Helpers.JsonOptions)!;

        Assert.Equal(2, result.TotalImportsAnalyzed);
    }

    [Fact]
    public async Task SlnxDetectBuildIssues_WorksOnNet8()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B", outputType: "Exe", references: "A");
        var slnx = builder.BuildSlnx();

        var json = await BuildIssueTools.DetectBuildIssues(slnx);
        var result = JsonSerializer.Deserialize<BuildIssueAnalysis>(json, Helpers.JsonOptions)!;

        Assert.Empty(result.Errors);
    }

    // --- .sln/.slnx produce same project set ---

    [Fact]
    public async Task SlnAndSlnx_ProduceIdenticalProjectLists()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Alpha").AddProject("Beta").AddProject("Gamma");
        var sln = builder.BuildSln();
        var slnx = builder.BuildSlnx();

        var rSln = Parse(await SolutionTools.AnalyzeSolution(sln, CancellationToken.None));
        var rSlnx = Parse(await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None));

        var slnNames = rSln.Projects.Select(p => p.Name).OrderBy(n => n);
        var slnxNames = rSlnx.Projects.Select(p => p.Name).OrderBy(n => n);
        Assert.Equal(slnNames, slnxNames);
    }

    // --- Empty .slnx ---

    [Fact]
    public async Task EmptySlnx_NoProjects()
    {
        using var builder = new TempSolutionBuilder();
        var slnx = builder.BuildSlnx();

        var result = Parse(await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None));

        Assert.Empty(result.Projects);
        Assert.Empty(result.Errors);
    }

    // --- Deep nesting: many projects in solution ---

    [Fact]
    public async Task FifteenProjects_AllDiscovered()
    {
        using var builder = new TempSolutionBuilder();
        for (int i = 0; i < 15; i++)
            builder.AddProject($"P{i:D2}");
        var slnx = builder.BuildSlnx();

        var result = Parse(await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None));

        Assert.Equal(15, result.ProjectCount);
        Assert.Empty(result.Errors);
    }
}
