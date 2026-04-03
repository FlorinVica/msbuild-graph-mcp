using System.Text.Json;
using Microsoft.Build.Construction;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>
/// .sln/.slnx parser edge cases from dotnet/msbuild#5027, #10012.
/// BOM headers, format detection, ResolveEntryPointsAsync.
/// </summary>
public class SolutionParserTests
{
    private static SolutionAnalysis ParseAnalysis(string json) =>
        JsonSerializer.Deserialize<SolutionAnalysis>(json, Helpers.JsonOptions)!;

    // --- .sln with UTF-8 BOM ---

    [Fact]
    public async Task SlnWithBom_ParsesCorrectly()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var sln = builder.BuildSln();

        // Prepend UTF-8 BOM
        var bytes = File.ReadAllBytes(sln);
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var withBom = bom.Concat(bytes).ToArray();
        File.WriteAllBytes(sln, withBom);

        var result = ParseAnalysis(await SolutionTools.AnalyzeSolution(sln, CancellationToken.None));

        Assert.Single(result.Projects);
    }

    // --- .slnx passed to SolutionFile.Parse throws (#10012) ---

    [Fact]
    public void SlnxPassedToSolutionFileParse_Throws()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var slnx = builder.BuildSlnx();

        // SolutionFile.Parse does NOT support .slnx
        Assert.Throws<Microsoft.Build.Exceptions.InvalidProjectFileException>(() =>
            SolutionFile.Parse(slnx));
    }

    // --- ResolveEntryPointsAsync: .slnx with relative paths ---

    [Fact]
    public async Task ResolveEntryPoints_Slnx_RelativePathsResolved()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Sub");
        var slnx = builder.BuildSlnx();

        var entryPoints = await Helpers.ResolveEntryPointsAsync(
            slnx, new Dictionary<string, string>(), CancellationToken.None);

        Assert.Single(entryPoints);
        // Path should be absolute, not relative
        Assert.True(Path.IsPathRooted(entryPoints[0].ProjectFile));
        Assert.Contains("Sub", entryPoints[0].ProjectFile);
    }

    // --- ResolveEntryPointsAsync: .sln passthrough ---

    [Fact]
    public async Task ResolveEntryPoints_Sln_PassedThrough()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var sln = builder.BuildSln();

        var entryPoints = await Helpers.ResolveEntryPointsAsync(
            sln, new Dictionary<string, string>(), CancellationToken.None);

        // .sln is passed through as single entry point (ProjectGraph parses it internally)
        Assert.Single(entryPoints);
        Assert.Equal(sln, entryPoints[0].ProjectFile);
    }

    // --- ResolveEntryPointsAsync: .csproj passthrough ---

    [Fact]
    public async Task ResolveEntryPoints_Csproj_PassedThrough()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        builder.BuildSln();

        var projPath = builder.GetProjectPath("Lib");
        var entryPoints = await Helpers.ResolveEntryPointsAsync(
            projPath, new Dictionary<string, string>(), CancellationToken.None);

        Assert.Single(entryPoints);
        Assert.Equal(projPath, entryPoints[0].ProjectFile);
    }

    // --- ResolveEntryPointsAsync: global properties propagated ---

    [Fact]
    public async Task ResolveEntryPoints_GlobalPropsIncluded()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var slnx = builder.BuildSlnx();

        var globalProps = new Dictionary<string, string> { ["Configuration"] = "Release" };
        var entryPoints = await Helpers.ResolveEntryPointsAsync(
            slnx, globalProps, CancellationToken.None);

        Assert.Single(entryPoints);
        Assert.True(entryPoints[0].GlobalProperties.ContainsKey("Configuration"));
        Assert.Equal("Release", entryPoints[0].GlobalProperties["Configuration"]);
    }

    // --- Empty .slnx ---

    [Fact]
    public async Task ResolveEntryPoints_EmptySlnx_ReturnsEmpty()
    {
        using var builder = new TempSolutionBuilder();
        var slnx = builder.BuildSlnx(); // empty solution

        var entryPoints = await Helpers.ResolveEntryPointsAsync(
            slnx, new Dictionary<string, string>(), CancellationToken.None);

        Assert.Empty(entryPoints);
    }

    // --- Format detection by extension ---

    [Fact]
    public async Task FormatDetection_SlnReportsSln()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var sln = builder.BuildSln();

        var result = ParseAnalysis(await SolutionTools.AnalyzeSolution(sln, CancellationToken.None));
        Assert.Equal("sln", result.Format);
    }

    [Fact]
    public async Task FormatDetection_SlnxReportsSlnx()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var slnx = builder.BuildSlnx();

        var result = ParseAnalysis(await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None));
        Assert.Equal("slnx", result.Format);
    }
}
