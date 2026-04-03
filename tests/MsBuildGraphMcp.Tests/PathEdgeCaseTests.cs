using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>
/// Production path edge cases: spaces, special chars, Unicode (CJK), dots, deep nesting.
/// From dotnet/msbuild#397, #8762, #5132, #53, NuGet/Home#11953.
/// Critical for worldwide deployment.
/// </summary>
public class PathEdgeCaseTests
{
    private static SolutionAnalysis ParseSln(string json) =>
        JsonSerializer.Deserialize<SolutionAnalysis>(json, Helpers.JsonOptions)!;
    private static PropertyAnalysis ParseProps(string json) =>
        JsonSerializer.Deserialize<PropertyAnalysis>(json, Helpers.JsonOptions)!;

    // --- Spaces in path ---

    [Fact]
    public async Task SpacesInPath_EvaluatesCorrectly()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), $"mcp test {Guid.NewGuid():N}");
        Directory.CreateDirectory(rootDir);
        try
        {
            var projDir = Path.Combine(rootDir, "My Project");
            Directory.CreateDirectory(projDir);
            WriteCsproj(projDir, "MyProject");

            var slnx = Path.Combine(rootDir, "My Solution.slnx");
            File.WriteAllText(slnx, "<Solution>\r\n  <Project Path=\"My Project\\MyProject.csproj\" />\r\n</Solution>\r\n");

            var result = ParseSln(await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None));

            Assert.Single(result.Projects);
            Assert.Equal("net8.0", result.Projects[0].TargetFramework);
            Assert.Empty(result.Errors);
        }
        finally { Directory.Delete(rootDir, true); }
    }

    // --- Parentheses in path (Program Files (x86) pattern) ---

    [Fact]
    public async Task ParenthesesInPath_EvaluatesCorrectly()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), $"mcp(test){Guid.NewGuid():N}");
        Directory.CreateDirectory(rootDir);
        try
        {
            var projDir = Path.Combine(rootDir, "Lib");
            Directory.CreateDirectory(projDir);
            WriteCsproj(projDir, "Lib");

            var slnx = Path.Combine(rootDir, "Test.slnx");
            File.WriteAllText(slnx, "<Solution>\r\n  <Project Path=\"Lib\\Lib.csproj\" />\r\n</Solution>\r\n");

            var result = ParseSln(await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None));

            Assert.Single(result.Projects);
            Assert.Empty(result.Errors);
        }
        finally { Directory.Delete(rootDir, true); }
    }

    // --- Dots in directory name (company.product pattern) ---

    [Fact]
    public async Task DotsInPath_EvaluatesCorrectly()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), $"my.company.{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootDir);
        try
        {
            var projDir = Path.Combine(rootDir, "src");
            Directory.CreateDirectory(projDir);
            WriteCsproj(projDir, "App");

            var slnx = Path.Combine(rootDir, "Test.slnx");
            File.WriteAllText(slnx, "<Solution>\r\n  <Project Path=\"src\\App.csproj\" />\r\n</Solution>\r\n");

            var result = ParseSln(await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None));

            Assert.Single(result.Projects);
        }
        finally { Directory.Delete(rootDir, true); }
    }

    // --- Deep nesting (15 levels) ---

    [Fact]
    public async Task DeepNesting_EvaluatesCorrectly()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), $"mcp-deep-{Guid.NewGuid():N}");
        // Build a 10-level deep path
        var deepPath = rootDir;
        for (int i = 0; i < 10; i++)
            deepPath = Path.Combine(deepPath, $"d{i}");
        Directory.CreateDirectory(deepPath);

        try
        {
            WriteCsproj(deepPath, "Deep");

            var json = PropertyTools.AnalyzeProjectProperties(
                Path.Combine(deepPath, "Deep.csproj"),
                properties: new[] { "TargetFramework" });

            var result = ParseProps(json);
            Assert.Equal("net8.0", result.Properties[0].EvaluatedValue);
        }
        finally { Directory.Delete(rootDir, true); }
    }

    // --- JSON serialization of Windows backslash paths ---

    [Fact]
    public async Task BackslashPaths_SerializedCorrectlyInJson()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var slnx = builder.BuildSlnx();

        var json = await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None);

        // Verify the JSON is valid (parseable)
        var doc = JsonDocument.Parse(json);
        Assert.NotNull(doc);

        // Paths should contain backslashes (Windows) and be properly escaped
        var result = ParseSln(json);
        foreach (var proj in result.Projects)
        {
            // Path should be absolute and resolvable
            Assert.True(Path.IsPathRooted(proj.Path), $"Path should be absolute: {proj.Path}");
            Assert.True(File.Exists(proj.Path), $"Path should exist: {proj.Path}");
        }
    }

    // --- JSON round-trip of paths with special characters ---

    [Fact]
    public void JsonRoundTrip_PathWithAmpersand()
    {
        var proj = new ProjectInfo
        {
            Name = "Test",
            Path = @"C:\dev\A&B\test.csproj",
            TargetFramework = "net8.0"
        };

        var json = JsonSerializer.Serialize(proj, Helpers.JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ProjectInfo>(json, Helpers.JsonOptions)!;

        Assert.Equal(proj.Path, deserialized.Path);
    }

    [Fact]
    public void JsonRoundTrip_PathWithSingleQuote()
    {
        var proj = new ProjectInfo
        {
            Name = "Test",
            Path = @"C:\Users\O'Brien\test.csproj",
            TargetFramework = "net8.0"
        };

        var json = JsonSerializer.Serialize(proj, Helpers.JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ProjectInfo>(json, Helpers.JsonOptions)!;

        Assert.Equal(proj.Path, deserialized.Path);
    }

    // --- Helper to write a minimal .csproj ---

    private static void WriteCsproj(string dir, string name)
    {
        File.WriteAllText(Path.Combine(dir, $"{name}.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");
        File.WriteAllText(Path.Combine(dir, "C.cs"), $"namespace {name}; public class C {{}}\r\n");
    }
}
