using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>Edge cases for list_projects: empty, mixed extensions, .sln format, format detection.</summary>
public class ListProjectsEdgeCaseTests
{
    private static ProjectListResult Parse(string json) =>
        JsonSerializer.Deserialize<ProjectListResult>(json, Helpers.JsonOptions)!;

    [Fact]
    public async Task ListProjects_EmptySolution()
    {
        using var builder = new TempSolutionBuilder();
        var slnx = builder.BuildSlnx();

        var result = Parse(await ProjectListTools.ListProjects(slnx));

        Assert.Equal(0, result.Count);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ListProjects_SlnFormat_Works()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var sln = builder.BuildSln();

        var result = Parse(await ProjectListTools.ListProjects(sln));

        Assert.Equal("sln", result.Format);
        Assert.Single(result.Projects);
    }

    [Fact]
    public async Task ListProjects_ExtensionDetected()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("CsLib");
        var slnx = builder.BuildSlnx();

        var result = Parse(await ProjectListTools.ListProjects(slnx));

        Assert.Equal(".csproj", result.Projects[0].Extension);
    }

    [Fact]
    public async Task ListProjects_ManyProjects_AllListed()
    {
        using var builder = new TempSolutionBuilder();
        for (int i = 0; i < 15; i++)
            builder.AddProject($"P{i:D2}");
        var slnx = builder.BuildSlnx();

        var result = Parse(await ProjectListTools.ListProjects(slnx));

        Assert.Equal(15, result.Count);
    }

    [Fact]
    public async Task ListProjects_AbsolutePaths()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var slnx = builder.BuildSlnx();

        var result = Parse(await ProjectListTools.ListProjects(slnx));

        Assert.True(Path.IsPathRooted(result.Projects[0].Path));
    }

    [Fact]
    public async Task ListProjects_InvalidPath_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => ProjectListTools.ListProjects(@"C:\nonexistent.sln"));
    }

    [Fact]
    public async Task ListProjects_OutputIsValidJson()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B");
        var slnx = builder.BuildSlnx();

        var json = await ProjectListTools.ListProjects(slnx);
        var doc = JsonDocument.Parse(json);
        Assert.NotNull(doc);
    }
}
