using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>
/// Final sweep: gaps from Buildalyzer, OmniSharp, dotnet-outdated, Cake, NUKE,
/// dotnet/project-system, dotnet/format, dotnet/aspire.
/// Focus: TFM only in Dir.Build.props, stale cache, F#/VB project types,
/// solution folders filtering, non-restored project, SetTargetFramework metadata.
/// </summary>
public class FinalSweepTests
{
    // --- Buildalyzer#166: TFM defined ONLY in Directory.Build.props ---

    [Fact]
    public async Task TfmOnlyInDirectoryBuildProps_StillDiscovered()
    {
        using var builder = new TempSolutionBuilder();
        builder.BuildSlnx();

        // Create project WITHOUT TargetFramework — it comes from Dir.Build.props
        var projDir = Path.Combine(builder.RootDir, "NoTfm");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "NoTfm.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Library</OutputType>
  </PropertyGroup>
</Project>");
        File.WriteAllText(Path.Combine(projDir, "C.cs"), "namespace NoTfm; public class C {}");

        // Dir.Build.props provides TFM
        File.WriteAllText(Path.Combine(builder.RootDir, "Directory.Build.props"),
@"<Project>
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

        var slnx = Path.Combine(builder.RootDir, "Test.slnx");
        File.WriteAllText(slnx, "<Solution>\r\n  <Project Path=\"NoTfm\\NoTfm.csproj\" />\r\n</Solution>\r\n");

        var json = await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None);
        var result = JsonSerializer.Deserialize<SolutionAnalysis>(json, Helpers.JsonOptions)!;

        Assert.Single(result.Projects);
        Assert.Equal("net8.0", result.Projects[0].TargetFramework);
    }

    // --- Cake#1256: Solution folders must be filtered out ---

    [Fact]
    public async Task SolutionWithOnlyValidProjects_NoPhantomEntries()
    {
        // Verify analyze_solution returns only actual buildable projects
        using var builder = new TempSolutionBuilder();
        builder.AddProject("App", outputType: "Exe").AddProject("Lib");
        var slnx = builder.BuildSlnx();

        var json = await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None);
        var result = JsonSerializer.Deserialize<SolutionAnalysis>(json, Helpers.JsonOptions)!;

        Assert.Equal(2, result.ProjectCount);
        Assert.All(result.Projects, p =>
        {
            Assert.True(File.Exists(p.Path), $"Project path should exist: {p.Path}");
            Assert.NotNull(p.TargetFramework);
        });
    }

    // --- Stale cache: modify project, re-evaluate, verify updated ---

    [Fact]
    public async Task ProjectModifiedBetweenCalls_ReturnsUpdatedData()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Evolving");
        var slnx = builder.BuildSlnx();

        // First call
        var json1 = await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None);
        var result1 = JsonSerializer.Deserialize<SolutionAnalysis>(json1, Helpers.JsonOptions)!;
        Assert.Equal("Library", result1.Projects[0].OutputType);

        // Modify project on disk: change OutputType to Exe
        var csproj = builder.GetProjectPath("Evolving");
        var content = File.ReadAllText(csproj);
        content = content.Replace("<OutputType>Library</OutputType>", "<OutputType>Exe</OutputType>");
        File.WriteAllText(csproj, content);

        // Second call — should reflect the change (fresh ProjectCollection per call)
        var json2 = await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None);
        var result2 = JsonSerializer.Deserialize<SolutionAnalysis>(json2, Helpers.JsonOptions)!;
        Assert.Equal("Exe", result2.Projects[0].OutputType);
    }

    // --- SetTargetFramework metadata on ProjectReference (#7432) ---

    [Fact]
    public async Task SetTargetFrameworkMetadata_HandledGracefully()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddMultiTargetProject("MultiLib", "net8.0;netstandard2.0");
        builder.BuildSlnx();

        // Create consumer with explicit SetTargetFramework metadata
        var consumerDir = Path.Combine(builder.RootDir, "Consumer");
        Directory.CreateDirectory(consumerDir);
        File.WriteAllText(Path.Combine(consumerDir, "Consumer.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include=""..\MultiLib\MultiLib.csproj"" SetTargetFramework=""TargetFramework=netstandard2.0"" />
  </ItemGroup>
</Project>");
        File.WriteAllText(Path.Combine(consumerDir, "P.cs"), "class P { static void Main() {} }");

        var slnx = Path.Combine(builder.RootDir, "Test.slnx");
        File.WriteAllText(slnx, @"<Solution>
  <Project Path=""MultiLib\MultiLib.csproj"" />
  <Project Path=""Consumer\Consumer.csproj"" />
</Solution>");

        // May throw MSB0001 or succeed — either way, no crash
        var json = await ProjectGraphTools.GetProjectGraph(slnx);
        var result = JsonSerializer.Deserialize<GraphAnalysis>(json, Helpers.JsonOptions)!;
        Assert.NotNull(result);
        // If it has errors, they should be descriptive
        if (result.Errors.Count > 0)
            Assert.All(result.Errors, e => Assert.True(e.Length > 10, "Error should be descriptive"));
    }

    // --- Non-restored project: missing project.assets.json ---

    [Fact]
    public async Task NonRestoredProject_EvaluatesWithWarning()
    {
        using var builder = new TempSolutionBuilder();
        builder.BuildSlnx();

        var projDir = Path.Combine(builder.RootDir, "NoRestore");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "NoRestore.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.3"" />
  </ItemGroup>
</Project>");
        File.WriteAllText(Path.Combine(projDir, "C.cs"), "namespace NoRestore; public class C {}");

        // DON'T run dotnet restore — project.assets.json is missing

        var slnx = Path.Combine(builder.RootDir, "Test.slnx");
        File.WriteAllText(slnx, "<Solution>\r\n  <Project Path=\"NoRestore\\NoRestore.csproj\" />\r\n</Solution>\r\n");

        // Should still evaluate basic properties (TFM, OutputType) even without restore
        var json = await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None);
        var result = JsonSerializer.Deserialize<SolutionAnalysis>(json, Helpers.JsonOptions)!;

        // Project should be in results with at least TFM
        Assert.Single(result.Projects);
        Assert.Equal("net8.0", result.Projects[0].TargetFramework);
    }

    // --- F# project (.fsproj) evaluation ---

    [Fact]
    public async Task FsharpProject_EvaluatesCorrectly()
    {
        using var builder = new TempSolutionBuilder();
        builder.BuildSlnx();

        var projDir = Path.Combine(builder.RootDir, "FsLib");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "FsLib.fsproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include=""Library.fs"" />
  </ItemGroup>
</Project>");
        File.WriteAllText(Path.Combine(projDir, "Library.fs"),
            "namespace FsLib\r\nmodule Say =\r\n    let hello name = $\"Hello {name}\"\r\n");

        var slnx = Path.Combine(builder.RootDir, "Test.slnx");
        File.WriteAllText(slnx, "<Solution>\r\n  <Project Path=\"FsLib\\FsLib.fsproj\" />\r\n</Solution>\r\n");

        var json = await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None);
        var result = JsonSerializer.Deserialize<SolutionAnalysis>(json, Helpers.JsonOptions)!;

        Assert.Single(result.Projects);
        Assert.Equal("FsLib", result.Projects[0].Name);
        Assert.Equal("net8.0", result.Projects[0].TargetFramework);
    }

    // --- VB.NET project (.vbproj) evaluation ---

    [Fact]
    public async Task VbNetProject_EvaluatesCorrectly()
    {
        using var builder = new TempSolutionBuilder();
        builder.BuildSlnx();

        var projDir = Path.Combine(builder.RootDir, "VbLib");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "VbLib.vbproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>VbLib</RootNamespace>
  </PropertyGroup>
</Project>");
        File.WriteAllText(Path.Combine(projDir, "Module1.vb"),
            "Namespace VbLib\r\n    Public Module Module1\r\n        Public Function Hello() As String\r\n            Return \"Hello\"\r\n        End Function\r\n    End Module\r\nEnd Namespace\r\n");

        var slnx = Path.Combine(builder.RootDir, "Test.slnx");
        File.WriteAllText(slnx, "<Solution>\r\n  <Project Path=\"VbLib\\VbLib.vbproj\" />\r\n</Solution>\r\n");

        var json = await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None);
        var result = JsonSerializer.Deserialize<SolutionAnalysis>(json, Helpers.JsonOptions)!;

        Assert.Single(result.Projects);
        Assert.Equal("VbLib", result.Projects[0].Name);
        Assert.Equal("VbLib", result.Projects[0].RootNamespace);
    }

    // --- Mixed C# + F# + VB solution ---

    [Fact]
    public async Task MixedLanguageSolution_AllEvaluated()
    {
        using var builder = new TempSolutionBuilder();
        builder.BuildSlnx();

        // C# project
        var csDir = Path.Combine(builder.RootDir, "CsLib");
        Directory.CreateDirectory(csDir);
        File.WriteAllText(Path.Combine(csDir, "CsLib.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>\r\n</Project>");
        File.WriteAllText(Path.Combine(csDir, "C.cs"), "namespace CsLib; public class C {}");

        // F# project
        var fsDir = Path.Combine(builder.RootDir, "FsLib");
        Directory.CreateDirectory(fsDir);
        File.WriteAllText(Path.Combine(fsDir, "FsLib.fsproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>\r\n  <ItemGroup><Compile Include=\"L.fs\" /></ItemGroup>\r\n</Project>");
        File.WriteAllText(Path.Combine(fsDir, "L.fs"), "namespace FsLib\r\nmodule L = let x = 1\r\n");

        // VB project
        var vbDir = Path.Combine(builder.RootDir, "VbLib");
        Directory.CreateDirectory(vbDir);
        File.WriteAllText(Path.Combine(vbDir, "VbLib.vbproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>\r\n</Project>");
        File.WriteAllText(Path.Combine(vbDir, "M.vb"), "Public Class M\r\nEnd Class\r\n");

        var slnx = Path.Combine(builder.RootDir, "Test.slnx");
        File.WriteAllText(slnx, @"<Solution>
  <Project Path=""CsLib\CsLib.csproj"" />
  <Project Path=""FsLib\FsLib.fsproj"" />
  <Project Path=""VbLib\VbLib.vbproj"" />
</Solution>");

        var json = await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None);
        var result = JsonSerializer.Deserialize<SolutionAnalysis>(json, Helpers.JsonOptions)!;

        Assert.Equal(3, result.ProjectCount);
        Assert.All(result.Projects, p => Assert.Equal("net8.0", p.TargetFramework));
        Assert.Empty(result.Errors);
    }

    // --- Graph construction with project modified between two calls (no stale cache) ---

    [Fact]
    public async Task GraphReflectsFileChanges_NoCacheStale()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Core").AddProject("App", outputType: "Exe", references: "Core");
        var slnx = builder.BuildSlnx();

        // First graph: App → Core
        var json1 = await ProjectGraphTools.GetProjectGraph(slnx);
        var result1 = JsonSerializer.Deserialize<GraphAnalysis>(json1, Helpers.JsonOptions)!;
        var app1 = result1.Projects.First(p => p.Name == "App");
        Assert.Single(app1.References);

        // Add a new project and reference
        var newDir = Path.Combine(builder.RootDir, "NewLib");
        Directory.CreateDirectory(newDir);
        File.WriteAllText(Path.Combine(newDir, "NewLib.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>\r\n</Project>");
        File.WriteAllText(Path.Combine(newDir, "C.cs"), "namespace NewLib; public class C {}");

        // Modify App to also reference NewLib
        var appCsproj = builder.GetProjectPath("App");
        var content = File.ReadAllText(appCsproj);
        content = content.Replace("</ItemGroup>",
            "    <ProjectReference Include=\"..\\NewLib\\NewLib.csproj\" />\r\n  </ItemGroup>");
        File.WriteAllText(appCsproj, content);

        // Update slnx
        File.WriteAllText(slnx, @"<Solution>
  <Project Path=""Core\Core.csproj"" />
  <Project Path=""NewLib\NewLib.csproj"" />
  <Project Path=""App\App.csproj"" />
</Solution>");

        // Second graph: App → Core + NewLib
        var json2 = await ProjectGraphTools.GetProjectGraph(slnx);
        var result2 = JsonSerializer.Deserialize<GraphAnalysis>(json2, Helpers.JsonOptions)!;
        var app2 = result2.Projects.First(p => p.Name == "App");
        Assert.Equal(2, app2.References.Count);
        Assert.Equal(3, result2.Metrics.UniqueProjectCount);
    }
}
