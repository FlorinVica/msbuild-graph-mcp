using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>
/// TFM edge cases from NuGet/Home issues: platform-specific TFMs, empty TFMs,
/// cross-family mismatches, netstandard compatibility matrix.
/// </summary>
public class TfmEdgeCaseTests
{
    private static BuildIssueAnalysis Parse(string json) =>
        JsonSerializer.Deserialize<BuildIssueAnalysis>(json, Helpers.JsonOptions)!;

    // --- Platform-specific TFMs ---

    [Fact]
    public async Task Net8Windows_ReferencingNet8_Compatible()
    {
        // net8.0-windows CAN consume net8.0 (platform-specific consumes generic)
        using var builder = new TempSolutionBuilder();
        builder.AddProject("GenericLib", tfm: "net8.0")
               .AddProject("WinApp", tfm: "net8.0-windows", outputType: "Exe", references: "GenericLib");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        Assert.Empty(result.FrameworkMismatches);
    }

    [Fact]
    public async Task Net8_ReferencingNet8Windows_Incompatible()
    {
        // net8.0 CANNOT consume net8.0-windows (generic cannot consume platform-specific)
        using var builder = new TempSolutionBuilder();
        builder.AddProject("WinLib", tfm: "net8.0-windows")
               .AddProject("GenericApp", tfm: "net8.0", outputType: "Exe", references: "WinLib");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        Assert.NotEmpty(result.FrameworkMismatches);
    }

    // --- netstandard compatibility matrix ---

    [Fact]
    public async Task Net48_ReferencingNetstandard20_Compatible()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("StdLib", tfm: "netstandard2.0")
               .AddProject("LegacyApp", tfm: "net48", outputType: "Exe", references: "StdLib");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        Assert.Empty(result.FrameworkMismatches);
    }

    [Fact]
    public async Task Netstandard20_ReferencingNet8_Incompatible()
    {
        // netstandard2.0 CANNOT consume net8.0
        using var builder = new TempSolutionBuilder();
        builder.AddProject("ModernLib", tfm: "net8.0")
               .AddProject("StdConsumer", tfm: "netstandard2.0", references: "ModernLib");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        Assert.NotEmpty(result.FrameworkMismatches);
    }

    // --- Cross-family mismatches ---

    [Fact]
    public async Task Net6_ReferencingNet48_Incompatible()
    {
        // .NET 6 CANNOT consume .NET Framework 4.8
        using var builder = new TempSolutionBuilder();
        builder.AddProject("FxLib", tfm: "net48")
               .AddProject("CoreApp", tfm: "net6.0", outputType: "Exe", references: "FxLib");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        Assert.NotEmpty(result.FrameworkMismatches);
    }

    // --- Newer referencing older (same family, compatible) ---

    [Theory]
    [InlineData("net9.0", "net8.0")]
    [InlineData("net8.0", "net6.0")]
    [InlineData("net8.0", "netstandard2.1")]
    public async Task NewerReferencingOlder_Compatible(string consumerTfm, string depTfm)
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Dep", tfm: depTfm)
               .AddProject("Consumer", tfm: consumerTfm, outputType: "Exe", references: "Dep");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        Assert.Empty(result.FrameworkMismatches);
    }

    // --- Older referencing newer (same family, incompatible) ---

    [Theory]
    [InlineData("net6.0", "net8.0")]
    [InlineData("net6.0", "net9.0")]
    public async Task OlderReferencingNewer_Incompatible(string consumerTfm, string depTfm)
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Dep", tfm: depTfm)
               .AddProject("Consumer", tfm: consumerTfm, outputType: "Exe", references: "Dep");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        Assert.NotEmpty(result.FrameworkMismatches);
    }

    // --- Empty TargetFramework (outer build node) ---

    [Fact]
    public async Task MultiTargetProject_OuterBuildNode_SkippedInTfmCheck()
    {
        // Multi-targeting project has outer build node with empty TF — should not cause false positive
        using var builder = new TempSolutionBuilder();
        builder.AddMultiTargetProject("Multi", "net8.0;net6.0")
               .AddProject("App", tfm: "net8.0", outputType: "Exe", references: "Multi");
        var slnx = builder.BuildSlnx();

        var result = Parse(await BuildIssueTools.DetectBuildIssues(slnx));

        // Should only flag genuine mismatches, not outer build node artifacts
        Assert.All(result.FrameworkMismatches, m =>
            Assert.False(string.IsNullOrEmpty(m.ProjectTfm),
                "Outer build node (empty TFM) should not appear in mismatches"));
    }
}
