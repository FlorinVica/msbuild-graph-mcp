using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>
/// JSON serialization round-trip tests for all model types.
/// Per MCP SDK best practice: all protocol types must survive serialization round-trips.
/// </summary>
public class JsonSerializationTests
{
    // --- SolutionAnalysis ---

    [Fact]
    public void SolutionAnalysis_RoundTrip_EmptyCollections()
    {
        var original = new SolutionAnalysis
        {
            SolutionPath = @"C:\test.sln",
            Format = "sln",
            Configurations = [],
            Projects = [],
            ExcludedProjects = [],
            Errors = []
        };

        var json = JsonSerializer.Serialize(original, Helpers.JsonOptions);
        var deserialized = JsonSerializer.Deserialize<SolutionAnalysis>(json, Helpers.JsonOptions)!;

        Assert.Equal(original.SolutionPath, deserialized.SolutionPath);
        Assert.Equal(0, deserialized.ProjectCount);
        Assert.Empty(deserialized.Errors);
    }

    [Fact]
    public void ProjectInfo_NullProperties_OmittedInJson()
    {
        var proj = new ProjectInfo
        {
            Name = "Test",
            Path = @"C:\test.csproj",
            TargetFramework = "net8.0",
            TargetFrameworks = null, // should be omitted
            OutputType = "Library",
            IsTestProject = null // should be omitted
        };

        var json = JsonSerializer.Serialize(proj, Helpers.JsonOptions);
        Assert.DoesNotContain("targetFrameworks", json);
        Assert.DoesNotContain("isTestProject", json);
        Assert.Contains("targetFramework", json);
    }

    // --- GraphAnalysis ---

    [Fact]
    public void GraphAnalysis_NullCircularError_OmittedInJson()
    {
        var graph = new GraphAnalysis
        {
            CircularDependencyError = null
        };

        var json = JsonSerializer.Serialize(graph, Helpers.JsonOptions);
        Assert.DoesNotContain("circularDependencyError", json);
    }

    [Fact]
    public void GraphAnalysis_WithCircularError_PresentInJson()
    {
        var graph = new GraphAnalysis
        {
            CircularDependencyError = "A -> B -> A"
        };

        var json = JsonSerializer.Serialize(graph, Helpers.JsonOptions);
        Assert.Contains("circularDependencyError", json);
        // The arrow characters may be encoded as unicode escapes in JSON
        var deserialized = JsonSerializer.Deserialize<GraphAnalysis>(json, Helpers.JsonOptions)!;
        Assert.Equal("A -> B -> A", deserialized.CircularDependencyError);
    }

    // --- BuildIssueAnalysis ---

    [Fact]
    public void BuildIssueAnalysis_TotalIssues_ComputedCorrectly()
    {
        var issues = new BuildIssueAnalysis
        {
            EntryPath = @"C:\test.sln",
            CircularDependencies = ["cycle1"],
            FrameworkMismatches = [new FrameworkMismatch
            {
                Project = "A", ProjectTfm = "net6.0",
                Dependency = "B", DependencyTfm = "net8.0",
                Reason = "incompatible"
            }],
            OrphanProjects = [new OrphanProject { Path = "C", Name = "C" }]
        };

        var json = JsonSerializer.Serialize(issues, Helpers.JsonOptions);
        var deserialized = JsonSerializer.Deserialize<BuildIssueAnalysis>(json, Helpers.JsonOptions)!;

        Assert.Equal(3, deserialized.TotalIssues);
    }

    // --- ConfigurationDiff ---

    [Fact]
    public void ConfigurationDiff_TotalDifferences_Computed()
    {
        var diff = new ConfigurationDiff
        {
            ProjectPath = "test",
            Config1 = "Debug",
            Config2 = "Release",
            Platform = "AnyCPU",
            PropertyDifferences = [new PropertyDifference { Name = "Opt", Value1 = "a", Value2 = "b" }],
            PackageReferenceDifferences = [new ItemDifference { ItemName = "Pkg", DiffType = "Only in Debug" }]
        };

        Assert.Equal(2, diff.TotalDifferences);

        var json = JsonSerializer.Serialize(diff, Helpers.JsonOptions);
        var rt = JsonSerializer.Deserialize<ConfigurationDiff>(json, Helpers.JsonOptions)!;
        Assert.Equal(2, rt.TotalDifferences);
    }

    // --- SharedImportAnalysis ---

    [Fact]
    public void SharedImport_ConsumerCount_Serialized()
    {
        var import = new SharedImport
        {
            ImportPath = "Dir.Build.props",
            ImportedBy = ["A", "B", "C"]
        };

        var json = JsonSerializer.Serialize(import, Helpers.JsonOptions);
        Assert.Contains("\"consumerCount\": 3", json);
    }

    // --- PropertyDetail with all nulls ---

    [Fact]
    public void PropertyDetail_AllNulls_MinimalJson()
    {
        var prop = new PropertyDetail
        {
            Name = "Missing",
            EvaluatedValue = null,
            UnevaluatedValue = null,
            SourceFile = null,
            SourceLine = null,
            PreviousValue = null
        };

        var json = JsonSerializer.Serialize(prop, Helpers.JsonOptions);

        // Name should be present, nulls should be omitted
        Assert.Contains("\"name\"", json);
        Assert.DoesNotContain("evaluatedValue", json);
        Assert.DoesNotContain("sourceFile", json);
        Assert.DoesNotContain("previousValue", json);
    }

    // --- camelCase naming ---

    [Fact]
    public void AllModels_UseCamelCaseNaming()
    {
        var graph = new GraphAnalysis
        {
            Metrics = new GraphMetrics { NodeCount = 5, EdgeCount = 3 }
        };

        var json = JsonSerializer.Serialize(graph, Helpers.JsonOptions);

        Assert.Contains("nodeCount", json);
        Assert.Contains("edgeCount", json);
        Assert.DoesNotContain("NodeCount", json);
        Assert.DoesNotContain("EdgeCount", json);
    }
}
