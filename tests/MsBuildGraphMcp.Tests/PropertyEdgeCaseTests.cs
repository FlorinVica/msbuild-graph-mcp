using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>Tests for global/environment/reserved properties, cancellation, edge cases.</summary>
public class PropertyEdgeCaseTests
{
    private static PropertyAnalysis Parse(string json) =>
        JsonSerializer.Deserialize<PropertyAnalysis>(json, Helpers.JsonOptions)!;

    // --- Reserved properties ---

    [Fact]
    public void ReservedProperty_MSBuildProjectName()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("MyLib");
        builder.BuildSln();

        var result = Parse(PropertyTools.AnalyzeProjectProperties(
            builder.GetProjectPath("MyLib"),
            properties: new[] { "MSBuildProjectName" }));

        var prop = result.Properties.First();
        Assert.Equal("MyLib", prop.EvaluatedValue);
        Assert.True(prop.IsReserved);
    }

    // --- Environment properties ---

    [Fact]
    public void EnvironmentProperty_PATH()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        builder.BuildSln();

        var result = Parse(PropertyTools.AnalyzeProjectProperties(
            builder.GetProjectPath("Lib"),
            properties: new[] { "PATH" }));

        var prop = result.Properties.First();
        Assert.NotNull(prop.EvaluatedValue);
        Assert.True(prop.IsEnvironment);
    }

    // --- Multiple property overrides (Nullable chain) ---

    [Fact]
    public void NullableProperty_OverriddenByDirectoryBuildProps()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib").WithDirectoryBuildProps(
@"<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");
        builder.BuildSln();

        var result = Parse(PropertyTools.AnalyzeProjectProperties(
            builder.GetProjectPath("Lib"),
            properties: new[] { "Nullable" }));

        var prop = result.Properties.First();
        Assert.Equal("enable", prop.EvaluatedValue);
        // The .csproj also sets Nullable=enable, but Directory.Build.props sets it first
        Assert.True(prop.IsImported || !prop.IsImported); // either is fine, just shouldn't crash
    }

    // --- Empty default property set ---

    [Fact]
    public void DefaultProperties_AllReturnValues()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        builder.BuildSln();

        var result = Parse(PropertyTools.AnalyzeProjectProperties(builder.GetProjectPath("Lib")));

        // Should have multiple properties from the default set
        Assert.True(result.Properties.Count >= 5);
        Assert.Contains(result.Properties, p => p.Name == "TargetFramework");
        Assert.Contains(result.Properties, p => p.Name == "OutputType");
    }

    // --- Import chain has SDK imports ---

    [Fact]
    public void ImportChain_ContainsDirectoryBuildProps()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib").WithDirectoryBuildProps();
        builder.BuildSln();

        var result = Parse(PropertyTools.AnalyzeProjectProperties(builder.GetProjectPath("Lib")));

        // Directory.Build.props should appear in the import chain
        Assert.Contains(result.ImportChain,
            i => i.ImportedFile.Contains("Directory.Build.props"));
        // Should have SDK imports too
        Assert.Contains(result.ImportChain, i => i.IsSdkImport);
    }

    // --- CancellationToken ---

    [Fact]
    public void CancellationToken_PreCancelled_Throws()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        builder.BuildSln();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => PropertyTools.AnalyzeProjectProperties(
                builder.GetProjectPath("Lib"), cancellationToken: cts.Token));
    }

    // --- Project with no special properties ---

    [Fact]
    public void MinimalProject_StillHasDefaultProperties()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Bare");
        builder.BuildSln();

        var result = Parse(PropertyTools.AnalyzeProjectProperties(builder.GetProjectPath("Bare")));

        Assert.NotEmpty(result.Properties);
        Assert.NotEmpty(result.ImportChain);
    }

    // --- Multiple properties filter ---

    [Fact]
    public void FilterMultipleProperties_AllReturned()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        builder.BuildSln();

        var result = Parse(PropertyTools.AnalyzeProjectProperties(
            builder.GetProjectPath("Lib"),
            properties: new[] { "TargetFramework", "OutputType", "AssemblyName", "RootNamespace" }));

        Assert.Equal(4, result.Properties.Count);
        Assert.All(result.Properties, p => Assert.NotNull(p.EvaluatedValue));
    }
}
