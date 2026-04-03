using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

public class SharedImportTests
{
    private static SharedImportAnalysis Parse(string json) =>
        JsonSerializer.Deserialize<SharedImportAnalysis>(json, Helpers.JsonOptions)!;

    // --- Directory.Build.props detection ---

    [Fact]
    public async Task DirectoryBuildProps_DetectedAsSpecialFile()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B").WithDirectoryBuildProps();
        var slnx = builder.BuildSlnx();

        var result = Parse(await SharedImportTools.FindSharedImports(slnx));

        Assert.Contains(result.SpecialFiles,
            sf => sf.FileType == "DirectoryBuildProps");
        var dbp = result.SpecialFiles.First(sf => sf.FileType == "DirectoryBuildProps");
        Assert.Equal(2, dbp.AffectedProjects.Count);
    }

    [Fact]
    public async Task WithoutDirectoryBuildProps_NoSpecialFiles()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B");
        var slnx = builder.BuildSlnx();

        var result = Parse(await SharedImportTools.FindSharedImports(slnx));

        Assert.DoesNotContain(result.SpecialFiles,
            sf => sf.FileType == "DirectoryBuildProps");
    }

    // --- Shared SDK imports ---

    [Fact]
    public async Task SdkImports_SharedAcrossAllProjects()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B").AddProject("C");
        var slnx = builder.BuildSlnx();

        var result = Parse(await SharedImportTools.FindSharedImports(slnx));

        // SDK .props/.targets should be shared by all 3 projects
        Assert.True(result.SharedImports.Count > 0);
        Assert.Contains(result.SharedImports, si => si.ConsumerCount == 3);
    }

    // --- Single project (nothing shared) ---

    [Fact]
    public async Task SingleProject_NoSharedImports()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Solo");
        var slnx = builder.BuildSlnx();

        var result = Parse(await SharedImportTools.FindSharedImports(slnx));

        // SharedImports only includes imports shared by 2+ projects
        Assert.Empty(result.SharedImports);
    }

    // --- TotalImportsAnalyzed ---

    [Fact]
    public async Task TotalImportsAnalyzed_MatchesProjectCount()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B").AddProject("C");
        var slnx = builder.BuildSlnx();

        var result = Parse(await SharedImportTools.FindSharedImports(slnx));

        Assert.Equal(3, result.TotalImportsAnalyzed);
    }

    // --- .sln entry ---

    [Fact]
    public async Task SlnEntry_Works()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B").WithDirectoryBuildProps();
        var sln = builder.BuildSln();

        var result = Parse(await SharedImportTools.FindSharedImports(sln));

        Assert.Equal(2, result.TotalImportsAnalyzed);
        Assert.Contains(result.SpecialFiles, sf => sf.FileType == "DirectoryBuildProps");
    }

    // --- .slnx entry ---

    [Fact]
    public async Task SlnxEntry_Works()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("X").AddProject("Y");
        var slnx = builder.BuildSlnx();

        var result = Parse(await SharedImportTools.FindSharedImports(slnx));

        Assert.Equal(2, result.TotalImportsAnalyzed);
        Assert.Empty(result.Errors);
    }

    // --- Directory.Packages.props detection ---

    [Fact]
    public async Task DirectoryPackagesProps_DetectedAsCPM()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B").WithDirectoryPackagesProps();
        var slnx = builder.BuildSlnx();

        var result = Parse(await SharedImportTools.FindSharedImports(slnx));

        Assert.Contains(result.SpecialFiles,
            sf => sf.FileType == "CentralPackageManagement");
    }

    // --- Validation ---

    [Fact]
    public async Task InvalidPath_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => SharedImportTools.FindSharedImports(@"C:\nonexistent.sln"));
    }
}
