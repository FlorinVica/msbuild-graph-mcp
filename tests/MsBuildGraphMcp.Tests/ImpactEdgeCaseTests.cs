using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>Edge cases for analyze_impact: orphan, self, deep chain, empty solution, concurrent.</summary>
public class ImpactEdgeCaseTests
{
    private static ImpactAnalysis Parse(string json) =>
        JsonSerializer.Deserialize<ImpactAnalysis>(json, Helpers.JsonOptions)!;

    [Fact]
    public async Task Impact_OrphanProject_ZeroDependents()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Orphan").AddProject("Other");
        var slnx = builder.BuildSlnx();

        var result = Parse(await ImpactTools.AnalyzeImpact(slnx, "Orphan"));

        Assert.Equal(0, result.TotalImpacted);
        Assert.Contains("Safe to remove", result.Summary);
    }

    [Fact]
    public async Task Impact_DeepChain_AllTransitivesFound()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("L0");
        for (int i = 1; i <= 4; i++)
            builder.AddProject($"L{i}", references: $"L{i - 1}");
        var slnx = builder.BuildSlnx();

        var result = Parse(await ImpactTools.AnalyzeImpact(slnx, "L0"));

        // L1 direct, L2-L4 transitive (or all direct depending on graph structure)
        Assert.True(result.TotalImpacted >= 4);
        Assert.True(result.DirectDependents.Count + result.TransitiveDependents.Count >= 4);
    }

    [Fact]
    public async Task Impact_EmptyTargetProject_Throws()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var slnx = builder.BuildSlnx();

        await Assert.ThrowsAsync<ArgumentException>(() => ImpactTools.AnalyzeImpact(slnx, ""));
    }

    [Fact]
    public async Task Impact_WhitespaceTargetProject_Throws()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var slnx = builder.BuildSlnx();

        await Assert.ThrowsAsync<ArgumentException>(() => ImpactTools.AnalyzeImpact(slnx, "   "));
    }

    [Fact]
    public async Task Impact_CaseInsensitiveMatch()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("MyLib").AddProject("App", outputType: "Exe", references: "MyLib");
        var slnx = builder.BuildSlnx();

        var result = Parse(await ImpactTools.AnalyzeImpact(slnx, "MYLIB"));

        Assert.True(result.TotalImpacted >= 1);
    }

    [Fact]
    public async Task Impact_SuggestionListOnNotFound()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Alpha").AddProject("Beta");
        var slnx = builder.BuildSlnx();

        var result = Parse(await ImpactTools.AnalyzeImpact(slnx, "NonExistent"));

        Assert.NotEmpty(result.Errors);
        Assert.Contains("Available:", result.Errors[0]);
        Assert.Contains("Alpha", result.Errors[0]);
    }

    [Fact]
    public async Task Impact_InvalidPath_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => ImpactTools.AnalyzeImpact(@"C:\nonexistent.sln", "Lib"));
    }

    [Fact]
    public async Task Impact_OutputIsValidJson()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib").AddProject("App", outputType: "Exe", references: "Lib");
        var slnx = builder.BuildSlnx();

        var json = await ImpactTools.AnalyzeImpact(slnx, "Lib");
        var doc = JsonDocument.Parse(json);
        Assert.NotNull(doc);
    }
}
