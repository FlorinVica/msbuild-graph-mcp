using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>Tests for excluded configurations, NullIfEmpty, multiple output types.</summary>
public class AnalyzeSolutionEdgeCaseTests
{
    private static SolutionAnalysis Parse(string json) =>
        JsonSerializer.Deserialize<SolutionAnalysis>(json, Helpers.JsonOptions)!;

    // --- Different output types ---

    [Theory]
    [InlineData("Library")]
    [InlineData("Exe")]
    [InlineData("WinExe")]
    public async Task OutputType_DetectedCorrectly(string outputType)
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Proj", outputType: outputType);
        var slnx = builder.BuildSlnx();

        var result = Parse(await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None));

        Assert.Equal(outputType, result.Projects[0].OutputType);
    }

    // --- Multiple TFMs ---

    [Fact]
    public async Task MultiTargetProject_ShowsTargetFrameworks()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddMultiTargetProject("Multi", "net8.0;net6.0");
        var slnx = builder.BuildSlnx();

        var result = Parse(await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None));

        var proj = result.Projects[0];
        Assert.Equal("net8.0;net6.0", proj.TargetFrameworks);
        // TargetFramework (singular) should be null for multi-target
        Assert.Null(proj.TargetFramework);
    }

    // --- Project with IsTestProject ---

    [Fact]
    public async Task TestProject_IsTestProjectFlagSet()
    {
        using var builder = new TempSolutionBuilder();
        builder.BuildSlnx(); // write base

        var testDir = Path.Combine(builder.RootDir, "Tests");
        Directory.CreateDirectory(testDir);
        File.WriteAllText(Path.Combine(testDir, "Tests.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
</Project>");
        File.WriteAllText(Path.Combine(testDir, "T.cs"), "namespace Tests;\npublic class T {}\n");

        var slnx = Path.Combine(builder.RootDir, "Test.slnx");
        File.WriteAllText(slnx, "<Solution>\r\n  <Project Path=\"Tests\\Tests.csproj\" />\r\n</Solution>\r\n");

        var result = Parse(await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None));

        Assert.Equal("true", result.Projects[0].IsTestProject);
    }

    // --- Large solution ---

    [Fact]
    public async Task TenProjects_AllEnumerated()
    {
        using var builder = new TempSolutionBuilder();
        for (int i = 0; i < 10; i++)
            builder.AddProject($"Proj{i}");
        var slnx = builder.BuildSlnx();

        var result = Parse(await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None));

        Assert.Equal(10, result.ProjectCount);
        Assert.Empty(result.Errors);
    }

    // --- Malformed project recovers ---

    [Fact]
    public async Task MalformedProject_ReportsError_ContinuesOthers()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Good");
        var slnx = builder.BuildSlnx();

        // Create a malformed project
        var badDir = Path.Combine(builder.RootDir, "Bad");
        Directory.CreateDirectory(badDir);
        File.WriteAllText(Path.Combine(badDir, "Bad.csproj"), "<Project><INVALID XML HERE");

        var content = File.ReadAllText(slnx);
        content = content.Replace("</Solution>",
            "  <Project Path=\"Bad\\Bad.csproj\" />\r\n</Solution>");
        File.WriteAllText(slnx, content);

        var result = Parse(await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None));

        Assert.Single(result.Projects); // Only Good
        Assert.Contains(result.Errors, e => e.Contains("Bad"));
    }

    // --- .sln with both .sln and .slnx outputs same data ---

    [Fact]
    public async Task SlnAndSlnx_SameProjects()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B");
        var sln = builder.BuildSln();
        var slnx = builder.BuildSlnx();

        var resultSln = Parse(await SolutionTools.AnalyzeSolution(sln, CancellationToken.None));
        var resultSlnx = Parse(await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None));

        Assert.Equal(resultSln.ProjectCount, resultSlnx.ProjectCount);
        var slnNames = resultSln.Projects.Select(p => p.Name).OrderBy(n => n).ToList();
        var slnxNames = resultSlnx.Projects.Select(p => p.Name).OrderBy(n => n).ToList();
        Assert.Equal(slnNames, slnxNames);
    }
}
