using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>
/// Tests for conditional ProjectReferences that vary by Configuration/Platform.
/// Based on dotnet/msbuild issue #13153 — conditional refs may be lost in graph eval.
/// </summary>
public class ConditionalReferenceTests
{
    private static GraphAnalysis ParseGraph(string json) =>
        JsonSerializer.Deserialize<GraphAnalysis>(json, Helpers.JsonOptions)!;

    // --- Conditional ProjectReference appears with matching config ---

    [Fact]
    public async Task ConditionalRef_AppearsWithMatchingConfig()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("ReleaseOnly");
        builder.BuildSlnx();

        // Create App with conditional ref on Release
        var appDir = Path.Combine(builder.RootDir, "App");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "App.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup Condition=""'$(Configuration)' == 'Release'"">
    <ProjectReference Include=""..\ReleaseOnly\ReleaseOnly.csproj"" />
  </ItemGroup>
</Project>");
        File.WriteAllText(Path.Combine(appDir, "P.cs"), "class P { static void Main() {} }");

        var slnx = Path.Combine(builder.RootDir, "Test.slnx");
        File.WriteAllText(slnx, @"<Solution>
  <Project Path=""ReleaseOnly\ReleaseOnly.csproj"" />
  <Project Path=""App\App.csproj"" />
</Solution>");

        // With Release — ref should exist
        var releaseResult = ParseGraph(await ProjectGraphTools.GetProjectGraph(slnx, configuration: "Release"));
        var appRelease = releaseResult.Projects.FirstOrDefault(p => p.Name == "App");
        Assert.NotNull(appRelease);
        Assert.Contains(appRelease!.References, r => r.Contains("ReleaseOnly"));
    }

    [Fact]
    public async Task ConditionalRef_AbsentWithNonMatchingConfig()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("ReleaseOnly");
        builder.BuildSlnx();

        var appDir = Path.Combine(builder.RootDir, "App");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "App.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup Condition=""'$(Configuration)' == 'Release'"">
    <ProjectReference Include=""..\ReleaseOnly\ReleaseOnly.csproj"" />
  </ItemGroup>
</Project>");
        File.WriteAllText(Path.Combine(appDir, "P.cs"), "class P { static void Main() {} }");

        var slnx = Path.Combine(builder.RootDir, "Test.slnx");
        File.WriteAllText(slnx, @"<Solution>
  <Project Path=""ReleaseOnly\ReleaseOnly.csproj"" />
  <Project Path=""App\App.csproj"" />
</Solution>");

        // With Debug — ref should NOT exist
        var debugResult = ParseGraph(await ProjectGraphTools.GetProjectGraph(slnx, configuration: "Debug"));
        var appDebug = debugResult.Projects.FirstOrDefault(p => p.Name == "App");
        Assert.NotNull(appDebug);
        Assert.DoesNotContain(appDebug!.References, r => r.Contains("ReleaseOnly"));
    }

    // --- Platform-conditional reference ---

    [Fact]
    public async Task PlatformConditionalRef_VariesByPlatform()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("WinOnly");
        builder.BuildSlnx();

        var appDir = Path.Combine(builder.RootDir, "App");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "App.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup Condition=""'$(Platform)' == 'x64'"">
    <ProjectReference Include=""..\WinOnly\WinOnly.csproj"" />
  </ItemGroup>
</Project>");
        File.WriteAllText(Path.Combine(appDir, "P.cs"), "class P { static void Main() {} }");

        var slnx = Path.Combine(builder.RootDir, "Test.slnx");
        File.WriteAllText(slnx, @"<Solution>
  <Project Path=""WinOnly\WinOnly.csproj"" />
  <Project Path=""App\App.csproj"" />
</Solution>");

        // With x64 — ref should exist
        var x64Result = ParseGraph(await ProjectGraphTools.GetProjectGraph(slnx, platform: "x64"));
        var appX64 = x64Result.Projects.FirstOrDefault(p => p.Name == "App");
        Assert.Contains(appX64!.References, r => r.Contains("WinOnly"));

        // Without platform (default AnyCPU) — ref should NOT exist
        var defaultResult = ParseGraph(await ProjectGraphTools.GetProjectGraph(slnx));
        var appDefault = defaultResult.Projects.FirstOrDefault(p => p.Name == "App");
        Assert.DoesNotContain(appDefault!.References, r => r.Contains("WinOnly"));
    }
}
