using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>Tests for Directory.Build.targets, missing projects, cancellation.</summary>
public class SharedImportEdgeCaseTests
{
    private static SharedImportAnalysis Parse(string json) =>
        JsonSerializer.Deserialize<SharedImportAnalysis>(json, Helpers.JsonOptions)!;

    // --- Directory.Build.targets detection ---

    [Fact]
    public async Task DirectoryBuildTargets_DetectedAsSpecialFile()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B");
        var slnx = builder.BuildSlnx();

        // Write Directory.Build.targets
        File.WriteAllText(Path.Combine(builder.RootDir, "Directory.Build.targets"),
            "<Project>\r\n</Project>\r\n");

        var result = Parse(await SharedImportTools.FindSharedImports(slnx));

        Assert.Contains(result.SpecialFiles,
            sf => sf.FileType == "DirectoryBuildTargets");
    }

    // --- Missing project in solution ---

    [Fact]
    public async Task MissingProject_ReportsErrorContinues()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Good");
        var slnx = builder.BuildSlnx();

        // Add ghost project to .slnx
        var content = File.ReadAllText(slnx);
        content = content.Replace("</Solution>",
            "  <Project Path=\"Missing\\Missing.csproj\" />\r\n</Solution>");
        File.WriteAllText(slnx, content);

        var result = Parse(await SharedImportTools.FindSharedImports(slnx));

        Assert.Equal(1, result.TotalImportsAnalyzed); // Only Good
        Assert.Contains(result.Errors, e => e.Contains("Missing"));
    }

    // --- Both Directory.Build.props AND targets ---

    [Fact]
    public async Task BothPropsAndTargets_BothDetected()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B").WithDirectoryBuildProps();
        var slnx = builder.BuildSlnx();

        File.WriteAllText(Path.Combine(builder.RootDir, "Directory.Build.targets"),
            "<Project>\r\n</Project>\r\n");

        var result = Parse(await SharedImportTools.FindSharedImports(slnx));

        Assert.Contains(result.SpecialFiles, sf => sf.FileType == "DirectoryBuildProps");
        Assert.Contains(result.SpecialFiles, sf => sf.FileType == "DirectoryBuildTargets");
    }

    // --- CancellationToken ---

    [Fact]
    public async Task CancellationToken_PreCancelled_Throws()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A");
        var slnx = builder.BuildSlnx();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SharedImportTools.FindSharedImports(slnx, cts.Token));
    }

    // --- Empty solution ---

    [Fact]
    public async Task EmptySolution_NoErrors()
    {
        using var builder = new TempSolutionBuilder();
        var slnx = builder.BuildSlnx();

        var result = Parse(await SharedImportTools.FindSharedImports(slnx));

        Assert.Equal(0, result.TotalImportsAnalyzed);
        Assert.Empty(result.SharedImports);
        Assert.Empty(result.SpecialFiles);
    }
}
