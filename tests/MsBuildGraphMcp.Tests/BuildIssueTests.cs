using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

public class BuildIssueTests
{
    private static BuildIssueAnalysis Parse(string json) =>
        JsonSerializer.Deserialize<BuildIssueAnalysis>(json, Helpers.JsonOptions)!;

    // --- Clean solution (no issues) ---

    [Fact]
    public async Task CleanSolution_ZeroIssues()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Core").AddProject("App", outputType: "Exe", references: "Core");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        Assert.Equal(0, result.TotalIssues);
        Assert.Empty(result.CircularDependencies);
        Assert.Empty(result.FrameworkMismatches);
        Assert.Empty(result.OrphanProjects);
        Assert.Empty(result.PlatformMismatches);
    }

    // --- Orphan project detection ---

    [Fact]
    public async Task OrphanLibrary_Detected()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Core")
               .AddProject("App", outputType: "Exe", references: "Core")
               .AddProject("Orphan");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        Assert.Single(result.OrphanProjects);
        Assert.Equal("Orphan", result.OrphanProjects[0].Name);
    }

    [Fact]
    public async Task OrphanExe_NotReported()
    {
        // Exe projects with no references to them are normal (entry points)
        using var builder = new TempSolutionBuilder();
        builder.AddProject("App1", outputType: "Exe").AddProject("App2", outputType: "Exe");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        Assert.Empty(result.OrphanProjects);
    }

    [Fact]
    public async Task MultipleOrphans_AllDetected()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Used").AddProject("App", outputType: "Exe", references: "Used")
               .AddProject("Orphan1").AddProject("Orphan2").AddProject("Orphan3");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        Assert.Equal(3, result.OrphanProjects.Count);
        var names = result.OrphanProjects.Select(o => o.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "Orphan1", "Orphan2", "Orphan3" }, names);
    }

    // --- TFM mismatch detection ---

    [Fact]
    public async Task TfmMismatch_OlderReferencingNewer_Detected()
    {
        // net6.0 project referencing net8.0 library = mismatch
        using var builder = new TempSolutionBuilder();
        builder.AddProject("NewLib", tfm: "net8.0")
               .AddProject("OldApp", tfm: "net6.0", outputType: "Exe", references: "NewLib");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        Assert.Single(result.FrameworkMismatches);
        Assert.Contains("net6.0", result.FrameworkMismatches[0].ProjectTfm);
        Assert.Contains("net8.0", result.FrameworkMismatches[0].DependencyTfm);
    }

    [Fact]
    public async Task TfmCompatible_NewerReferencingOlder_NoIssue()
    {
        // net8.0 referencing netstandard2.0 = compatible
        using var builder = new TempSolutionBuilder();
        builder.AddProject("StdLib", tfm: "netstandard2.0")
               .AddProject("App", tfm: "net8.0", outputType: "Exe", references: "StdLib");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        Assert.Empty(result.FrameworkMismatches);
    }

    [Fact]
    public async Task TfmMismatch_NetFrameworkReferencingNetCore_Detected()
    {
        // net48 cannot consume net8.0
        using var builder = new TempSolutionBuilder();
        builder.AddProject("ModernLib", tfm: "net8.0")
               .AddProject("LegacyApp", tfm: "net48", outputType: "Exe", references: "ModernLib");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        Assert.Single(result.FrameworkMismatches);
    }

    // --- Slnx entry point ---

    [Fact]
    public async Task SlnxEntry_Works()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib").AddProject("App", outputType: "Exe", references: "Lib");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        Assert.Equal(0, result.TotalIssues);
        Assert.Empty(result.Errors);
    }

    // --- Sln entry point ---

    [Fact]
    public async Task SlnEntry_Works()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib").AddProject("App", outputType: "Exe", references: "Lib");
        var sln = builder.BuildSln();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(sln));

        Assert.Empty(result.Errors);
    }

    // --- Validation ---

    [Fact]
    public async Task InvalidPath_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => BuildIssueTools.DetectBuildIssues(@"C:\nonexistent.sln"));
    }
}
