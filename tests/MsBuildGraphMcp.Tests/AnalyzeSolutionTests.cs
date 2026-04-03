using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

public class AnalyzeSolutionTests
{
    private static SolutionAnalysis Parse(string json) =>
        JsonSerializer.Deserialize<SolutionAnalysis>(json, Helpers.JsonOptions)!;

    // --- .sln format ---

    [Fact]
    public async Task Sln_SingleProject_ReturnsOneProject()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("MyLib");
        var slnPath = builder.BuildSln();

        var result = Parse(await SolutionTools.AnalyzeSolution(slnPath, CancellationToken.None));

        Assert.Equal("sln", result.Format);
        Assert.Single(result.Projects);
        Assert.Equal("MyLib", result.Projects[0].Name);
        Assert.Equal("Library", result.Projects[0].OutputType);
        Assert.Equal("net8.0", result.Projects[0].TargetFramework);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Sln_MultipleProjects_ReturnsAll()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Core").AddProject("Data", references: "Core")
               .AddProject("App", outputType: "Exe", references: "Data");
        var slnPath = builder.BuildSln();

        var result = Parse(await SolutionTools.AnalyzeSolution(slnPath, CancellationToken.None));

        Assert.Equal(3, result.ProjectCount);
        Assert.Contains(result.Projects, p => p.Name == "Core" && p.OutputType == "Library");
        Assert.Contains(result.Projects, p => p.Name == "App" && p.OutputType == "Exe");
    }

    [Fact]
    public async Task Sln_EmptySolution_ReturnsZeroProjects()
    {
        using var builder = new TempSolutionBuilder();
        var slnPath = builder.BuildSln();

        var result = Parse(await SolutionTools.AnalyzeSolution(slnPath, CancellationToken.None));

        Assert.Empty(result.Projects);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Sln_HasConfigurations()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var slnPath = builder.BuildSln();

        var result = Parse(await SolutionTools.AnalyzeSolution(slnPath, CancellationToken.None));

        Assert.Contains(result.Configurations, c => c.Contains("Debug"));
        Assert.Contains(result.Configurations, c => c.Contains("Release"));
    }

    [Fact]
    public async Task Slnx_MissingProjectFile_ReportsError()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Exists");
        var slnxPath = builder.BuildSlnx();

        // Add a ghost project entry to .slnx
        var content = File.ReadAllText(slnxPath);
        content = content.Replace("</Solution>",
            "  <Project Path=\"Ghost\\Ghost.csproj\" />\r\n</Solution>");
        File.WriteAllText(slnxPath, content);

        var result = Parse(await SolutionTools.AnalyzeSolution(slnxPath, CancellationToken.None));

        Assert.Single(result.Projects); // Only Exists
        Assert.Contains(result.Errors, e => e.Contains("Ghost"));
    }

    // --- .slnx format ---

    [Fact]
    public async Task Slnx_SingleProject_Works()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("MyLib");
        var slnxPath = builder.BuildSlnx();

        var result = Parse(await SolutionTools.AnalyzeSolution(slnxPath, CancellationToken.None));

        Assert.Equal("slnx", result.Format);
        Assert.Single(result.Projects);
        Assert.Equal("MyLib", result.Projects[0].Name);
    }

    [Fact]
    public async Task Slnx_MultipleProjects_ReturnsAll()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B").AddProject("C", outputType: "Exe");
        var slnxPath = builder.BuildSlnx();

        var result = Parse(await SolutionTools.AnalyzeSolution(slnxPath, CancellationToken.None));

        Assert.Equal(3, result.ProjectCount);
    }

    // --- SDK detection ---

    [Fact]
    public async Task DetectsSdkAttribute()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var slnPath = builder.BuildSln();

        var result = Parse(await SolutionTools.AnalyzeSolution(slnPath, CancellationToken.None));

        Assert.Equal("Microsoft.NET.Sdk", result.Projects[0].Sdk);
    }

    // --- Cancellation ---

    [Fact]
    public async Task CancellationToken_Respected()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B").AddProject("C");
        var slnPath = builder.BuildSln();

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Already cancelled

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => SolutionTools.AnalyzeSolution(slnPath, cts.Token));
    }

    // --- Validation ---

    [Fact]
    public async Task InvalidPath_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => SolutionTools.AnalyzeSolution(@"C:\nonexistent.sln", CancellationToken.None));
    }

    [Fact]
    public async Task WrongExtension_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => SolutionTools.AnalyzeSolution(@"C:\foo.txt", CancellationToken.None));
    }
}
