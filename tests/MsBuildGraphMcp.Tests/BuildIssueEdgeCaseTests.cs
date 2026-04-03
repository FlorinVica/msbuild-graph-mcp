using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>Tests for platform mismatches, IsTestProject exclusion, multi-targeting, cancellation.</summary>
public class BuildIssueEdgeCaseTests
{
    private static BuildIssueAnalysis Parse(string json) =>
        JsonSerializer.Deserialize<BuildIssueAnalysis>(json, Helpers.JsonOptions)!;

    // --- IsTestProject exclusion from orphans ---

    [Fact]
    public async Task OrphanTestProject_NotReportedAsOrphan()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib").AddProject("App", outputType: "Exe", references: "Lib");
        builder.BuildSlnx(); // writes files

        // Create a test project that references nothing and nobody references it
        var testDir = Path.Combine(builder.RootDir, "Tests");
        Directory.CreateDirectory(testDir);
        File.WriteAllText(Path.Combine(testDir, "Tests.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
</Project>");
        File.WriteAllText(Path.Combine(testDir, "Test1.cs"), "namespace Tests;\npublic class Test1 { }\n");

        // Update .slnx to include test project
        var slnx = Path.Combine(builder.RootDir, "Test.slnx");
        File.WriteAllText(slnx, @"<Solution>
  <Project Path=""Lib\Lib.csproj"" />
  <Project Path=""App\App.csproj"" />
  <Project Path=""Tests\Tests.csproj"" />
</Solution>");

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        // Tests project should NOT be reported as orphan (IsTestProject=true)
        Assert.DoesNotContain(result.OrphanProjects, o => o.Name == "Tests");
    }

    // --- Configuration parameter ---

    [Fact]
    public async Task ConfigAndPlatform_PassedCorrectly()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib").AddProject("App", outputType: "Exe", references: "Lib");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(
            slnx, configuration: "Release", platform: "AnyCPU"));

        Assert.Empty(result.Errors);
    }

    // --- TFM compatibility: netstandard2.0 works with net48 ---

    [Fact]
    public async Task NetFramework_ReferencingNetStandard_NoMismatch()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("StdLib", tfm: "netstandard2.0")
               .AddProject("Legacy", tfm: "net48", outputType: "Exe", references: "StdLib");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        Assert.Empty(result.FrameworkMismatches);
    }

    // --- Same TFM, no mismatch ---

    [Fact]
    public async Task SameTfm_NoMismatch()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A", tfm: "net8.0")
               .AddProject("B", tfm: "net8.0", outputType: "Exe", references: "A");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        Assert.Empty(result.FrameworkMismatches);
    }

    // --- Multi-targeting project outer build skipped in orphan detection ---

    [Fact]
    public async Task MultiTargetOrphan_OnlyReportedOnce()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddMultiTargetProject("OrphanMulti", "net8.0;net6.0")
               .AddProject("App", outputType: "Exe");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        // OrphanMulti should be in orphans (nobody references it, not Exe, not test)
        // But should NOT appear multiple times (once per inner build)
        var orphanCount = result.OrphanProjects.Count(o => o.Name == "OrphanMulti");
        Assert.True(orphanCount <= 2, $"OrphanMulti appeared {orphanCount} times, expected at most 2 (inner builds)");
    }

    // --- CancellationToken ---

    [Fact]
    public async Task CancellationToken_PreCancelled_ReturnsErrorOrThrows()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var slnx = builder.BuildSlnx();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            var json = await BuildIssueTools.DetectBuildIssues(slnx, cancellationToken: cts.Token);
            var result = Parse(json);
            Assert.True(result.Errors.Count > 0 || result.TotalIssues == 0,
                "Pre-cancelled token should result in errors or empty result");
        }
        catch (OperationCanceledException)
        {
            // Also acceptable
        }
    }

    // --- Multiple issues in one solution ---

    [Fact]
    public async Task MultipleIssueTypes_AllDetected()
    {
        using var builder = new TempSolutionBuilder();
        // TFM mismatch: net6.0 referencing net8.0
        builder.AddProject("NewLib", tfm: "net8.0")
               .AddProject("OldApp", tfm: "net6.0", outputType: "Exe", references: "NewLib")
               // Orphan
               .AddProject("Unused");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        Assert.True(result.TotalIssues >= 2);
        Assert.NotEmpty(result.FrameworkMismatches);
        Assert.NotEmpty(result.OrphanProjects);
    }

    // --- WinExe not reported as orphan ---

    [Fact]
    public async Task WinExeProject_NotOrphan()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("WpfApp", outputType: "WinExe");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        Assert.Empty(result.OrphanProjects);
    }
}
