using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>
/// Tests for v1.1 tools: analyze_impact, check_package_versions, get_build_order, list_projects.
/// </summary>
public class NewToolsTests
{
    // ===== analyze_impact =====

    [Fact]
    public async Task Impact_LeafProject_ZeroDependents()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Core").AddProject("App", outputType: "Exe", references: "Core");
        var slnx = builder.BuildSlnx();

        var json = await ImpactTools.AnalyzeImpact(slnx, "App");
        var result = JsonSerializer.Deserialize<ImpactAnalysis>(json, Helpers.JsonOptions)!;

        // App is root — nothing depends on it
        Assert.Equal(0, result.TotalImpacted);
        Assert.Contains("Safe to remove", result.Summary);
    }

    [Fact]
    public async Task Impact_CoreLibrary_AllDependentsFound()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Core")
               .AddProject("Data", references: "Core")
               .AddProject("Web", outputType: "Exe", references: "Data");
        var slnx = builder.BuildSlnx();

        var json = await ImpactTools.AnalyzeImpact(slnx, "Core");
        var result = JsonSerializer.Deserialize<ImpactAnalysis>(json, Helpers.JsonOptions)!;

        Assert.True(result.TotalImpacted >= 2); // Data (direct) + Web (transitive), possibly more inner builds
        Assert.NotEmpty(result.DirectDependents);
        Assert.Contains("impacts", result.Summary);
    }

    [Fact]
    public async Task Impact_DiamondDependency_NoDuplicates()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Core")
               .AddProject("Lib1", references: "Core")
               .AddProject("Lib2", references: "Core")
               .AddProject("App", outputType: "Exe", references: new[] { "Lib1", "Lib2" });
        var slnx = builder.BuildSlnx();

        var json = await ImpactTools.AnalyzeImpact(slnx, "Core");
        var result = JsonSerializer.Deserialize<ImpactAnalysis>(json, Helpers.JsonOptions)!;

        Assert.True(result.TotalImpacted >= 3); // Lib1, Lib2 (direct) + App (transitive)
        Assert.True(result.DirectDependents.Count >= 2);
    }

    [Fact]
    public async Task Impact_NonExistentProject_ReturnsError()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var slnx = builder.BuildSlnx();

        var json = await ImpactTools.AnalyzeImpact(slnx, "NonExistent");
        var result = JsonSerializer.Deserialize<ImpactAnalysis>(json, Helpers.JsonOptions)!;

        Assert.NotEmpty(result.Errors);
        Assert.Contains("not found", result.Errors[0]);
    }

    [Fact]
    public async Task Impact_NullTargetProject_Throws()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var slnx = builder.BuildSlnx();

        await Assert.ThrowsAsync<ArgumentException>(
            () => ImpactTools.AnalyzeImpact(slnx, null!));
    }

    [Fact]
    public async Task Impact_MatchesByFilename()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("MyLib").AddProject("App", outputType: "Exe", references: "MyLib");
        var slnx = builder.BuildSlnx();

        // Match by filename with extension
        var json = await ImpactTools.AnalyzeImpact(slnx, "MyLib.csproj");
        var result = JsonSerializer.Deserialize<ImpactAnalysis>(json, Helpers.JsonOptions)!;

        Assert.Equal(1, result.TotalImpacted);
    }

    // ===== check_package_versions =====

    [Fact]
    public async Task PackageVersions_ConsistentVersions_NoInconsistencies()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B");
        var slnx = builder.BuildSlnx();

        var json = await PackageVersionTools.CheckPackageVersions(slnx);
        var result = JsonSerializer.Deserialize<PackageVersionAnalysis>(json, Helpers.JsonOptions)!;

        Assert.Empty(result.Inconsistencies);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task PackageVersions_InconsistentVersions_Detected()
    {
        using var builder = new TempSolutionBuilder();
        builder.BuildSlnx();

        // Create two projects with different versions of same package
        foreach (var (name, ver) in new[] { ("ProjA", "13.0.1"), ("ProjB", "13.0.3") })
        {
            var dir = Path.Combine(builder.RootDir, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{name}.csproj"),
$@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include=""Newtonsoft.Json"" Version=""{ver}"" /></ItemGroup>
</Project>");
            File.WriteAllText(Path.Combine(dir, "C.cs"), $"namespace {name}; public class C {{}}");
        }

        var slnx = Path.Combine(builder.RootDir, "Test.slnx");
        File.WriteAllText(slnx, @"<Solution>
  <Project Path=""ProjA\ProjA.csproj"" />
  <Project Path=""ProjB\ProjB.csproj"" />
</Solution>");

        var json = await PackageVersionTools.CheckPackageVersions(slnx);
        var result = JsonSerializer.Deserialize<PackageVersionAnalysis>(json, Helpers.JsonOptions)!;

        Assert.Single(result.Inconsistencies);
        Assert.Equal("Newtonsoft.Json", result.Inconsistencies[0].PackageId);
        Assert.Equal(2, result.Inconsistencies[0].VersionCount);
    }

    [Fact]
    public async Task PackageVersions_CpmDetected()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib").WithDirectoryPackagesProps();
        var slnx = builder.BuildSlnx();

        var json = await PackageVersionTools.CheckPackageVersions(slnx);
        var result = JsonSerializer.Deserialize<PackageVersionAnalysis>(json, Helpers.JsonOptions)!;

        Assert.True(result.CpmEnabled);
    }

    // ===== get_build_order =====

    [Fact]
    public async Task BuildOrder_LinearChain_CorrectOrder()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Core")
               .AddProject("Data", references: "Core")
               .AddProject("App", outputType: "Exe", references: "Data");
        var slnx = builder.BuildSlnx();

        var json = await BuildOrderTools.GetBuildOrder(slnx);
        var result = JsonSerializer.Deserialize<BuildOrderAnalysis>(json, Helpers.JsonOptions)!;

        Assert.Equal(3, result.TotalProjects);
        Assert.Equal(2, result.CriticalPathLength);
        Assert.Equal("Core", result.BuildOrder[0]);
        Assert.Equal("App", result.BuildOrder[^1]);
    }

    [Fact]
    public async Task BuildOrder_SingleProject_DepthZero()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Solo");
        var slnx = builder.BuildSlnx();

        var json = await BuildOrderTools.GetBuildOrder(slnx);
        var result = JsonSerializer.Deserialize<BuildOrderAnalysis>(json, Helpers.JsonOptions)!;

        Assert.Equal(1, result.TotalProjects);
        Assert.Equal(0, result.CriticalPathLength);
    }

    [Fact]
    public async Task BuildOrder_CircularDependency_ReportsError()
    {
        using var builder = new TempSolutionBuilder();

        var dirA = Path.Combine(builder.RootDir, "A");
        var dirB = Path.Combine(builder.RootDir, "B");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        File.WriteAllText(Path.Combine(dirA, "A.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <ItemGroup><ProjectReference Include=""..\B\B.csproj"" /></ItemGroup>
</Project>");
        File.WriteAllText(Path.Combine(dirA, "C.cs"), "namespace A; public class C {}");

        File.WriteAllText(Path.Combine(dirB, "B.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <ItemGroup><ProjectReference Include=""..\A\A.csproj"" /></ItemGroup>
</Project>");
        File.WriteAllText(Path.Combine(dirB, "C.cs"), "namespace B; public class C {}");

        var json = await BuildOrderTools.GetBuildOrder(Path.Combine(dirA, "A.csproj"));
        var result = JsonSerializer.Deserialize<BuildOrderAnalysis>(json, Helpers.JsonOptions)!;

        Assert.NotNull(result.CircularDependencyError);
    }

    // ===== list_projects =====

    [Fact]
    public async Task ListProjects_ReturnsAllProjects()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B").AddProject("C");
        var slnx = builder.BuildSlnx();

        var json = await ProjectListTools.ListProjects(slnx);
        var result = JsonSerializer.Deserialize<ProjectListResult>(json, Helpers.JsonOptions)!;

        Assert.Equal(3, result.Count);
        Assert.All(result.Projects, p => Assert.True(p.ExistsOnDisk));
        Assert.All(result.Projects, p => Assert.Equal(".csproj", p.Extension));
    }

    [Fact]
    public async Task ListProjects_MissingProject_MarkedAsNotExisting()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Good");
        var slnx = builder.BuildSlnx();

        // Add ghost project
        var content = File.ReadAllText(slnx);
        content = content.Replace("</Solution>",
            "  <Project Path=\"Ghost\\Ghost.csproj\" />\r\n</Solution>");
        File.WriteAllText(slnx, content);

        var json = await ProjectListTools.ListProjects(slnx);
        var result = JsonSerializer.Deserialize<ProjectListResult>(json, Helpers.JsonOptions)!;

        Assert.Equal(2, result.Count);
        Assert.Contains(result.Projects, p => p.Name == "Good" && p.ExistsOnDisk);
        Assert.Contains(result.Projects, p => p.Name == "Ghost" && !p.ExistsOnDisk);
    }

    [Fact]
    public async Task ListProjects_SlnxFormat_DetectedCorrectly()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var slnx = builder.BuildSlnx();

        var json = await ProjectListTools.ListProjects(slnx);
        var result = JsonSerializer.Deserialize<ProjectListResult>(json, Helpers.JsonOptions)!;

        Assert.Equal("slnx", result.Format);
    }

    [Fact]
    public async Task ListProjects_NoEvaluation_FastResponse()
    {
        using var builder = new TempSolutionBuilder();
        for (int i = 0; i < 10; i++)
            builder.AddProject($"Proj{i}");
        var slnx = builder.BuildSlnx();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var json = await ProjectListTools.ListProjects(slnx);
        sw.Stop();

        var result = JsonSerializer.Deserialize<ProjectListResult>(json, Helpers.JsonOptions)!;
        Assert.Equal(10, result.Count);
        // No MSBuild evaluation — should be very fast
        Assert.True(sw.ElapsedMilliseconds < 2000, $"list_projects took {sw.ElapsedMilliseconds}ms — too slow for no-evaluation tool");
    }
}
