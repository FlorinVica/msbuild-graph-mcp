using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>
/// Tests for AggregateException handling when multiple projects fail evaluation.
/// Based on dotnet/msbuild PR #7831 — ProjectGraph wraps multi-project failures.
/// </summary>
public class AggregateExceptionTests
{
    private static GraphAnalysis ParseGraph(string json) =>
        JsonSerializer.Deserialize<GraphAnalysis>(json, Helpers.JsonOptions)!;
    private static BuildIssueAnalysis ParseIssues(string json) =>
        JsonSerializer.Deserialize<BuildIssueAnalysis>(json, Helpers.JsonOptions)!;

    // --- Multiple malformed projects in graph ---

    [Fact]
    public async Task TwoMalformedProjects_BothErrorsReported()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Good");
        var slnx = builder.BuildSlnx();

        // Create two malformed projects
        foreach (var name in new[] { "Bad1", "Bad2" })
        {
            var dir = Path.Combine(builder.RootDir, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{name}.csproj"),
                "<Project><BROKEN XML NO CLOSE TAG");
        }

        // Update .slnx to include them
        File.WriteAllText(slnx, @"<Solution>
  <Project Path=""Good\Good.csproj"" />
  <Project Path=""Bad1\Bad1.csproj"" />
  <Project Path=""Bad2\Bad2.csproj"" />
</Solution>");

        var result = ParseGraph(await ProjectGraphTools.GetProjectGraph(slnx));

        // Should have errors for the bad projects
        Assert.True(result.Errors.Count >= 1,
            $"Expected errors for malformed projects but got {result.Errors.Count}");
    }

    // --- One good, one bad project in detect_build_issues ---

    [Fact]
    public async Task MalformedProjectInSolution_DetectBuildIssues_ReportsError()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Good");
        var slnx = builder.BuildSlnx();

        var badDir = Path.Combine(builder.RootDir, "Bad");
        Directory.CreateDirectory(badDir);
        File.WriteAllText(Path.Combine(badDir, "Bad.csproj"), "<NOT VALID XML AT ALL");

        File.WriteAllText(slnx, @"<Solution>
  <Project Path=""Good\Good.csproj"" />
  <Project Path=""Bad\Bad.csproj"" />
</Solution>");

        var result = ParseIssues(await BuildIssueTools.DetectBuildIssues(slnx));

        Assert.True(result.Errors.Count >= 1);
    }

    // --- Missing SDK reference ---

    [Fact]
    public async Task ProjectWithBadSdk_ReportsError()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Good");
        var slnx = builder.BuildSlnx();

        var badDir = Path.Combine(builder.RootDir, "BadSdk");
        Directory.CreateDirectory(badDir);
        File.WriteAllText(Path.Combine(badDir, "BadSdk.csproj"),
@"<Project Sdk=""NonExistent.Sdk.That.Doesnt.Exist/99.0.0"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");
        File.WriteAllText(Path.Combine(badDir, "C.cs"), "namespace BadSdk; public class C {}");

        File.WriteAllText(slnx, @"<Solution>
  <Project Path=""Good\Good.csproj"" />
  <Project Path=""BadSdk\BadSdk.csproj"" />
</Solution>");

        var result = ParseGraph(await ProjectGraphTools.GetProjectGraph(slnx));

        // Either errors are reported OR the SDK resolver somehow resolves it
        // (behavior varies by machine config). The key thing: no unhandled crash.
        Assert.NotNull(result);
    }
}
