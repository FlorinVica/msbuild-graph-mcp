using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>Edge cases for check_package_versions: no packages, same versions, VersionOverride, missing project.</summary>
public class PackageVersionEdgeCaseTests
{
    private static PackageVersionAnalysis Parse(string json) =>
        JsonSerializer.Deserialize<PackageVersionAnalysis>(json, Helpers.JsonOptions)!;

    [Fact]
    public async Task PackageVersions_NoPackageReferences_EmptyResult()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Plain");
        var slnx = builder.BuildSlnx();

        var result = Parse(await PackageVersionTools.CheckPackageVersions(slnx));

        Assert.Equal(0, result.TotalPackages);
        Assert.Empty(result.Inconsistencies);
    }

    [Fact]
    public async Task PackageVersions_SameVersionEverywhere_NoInconsistency()
    {
        using var builder = new TempSolutionBuilder();
        builder.BuildSlnx();

        foreach (var name in new[] { "A", "B", "C" })
        {
            var dir = Path.Combine(builder.RootDir, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{name}.csproj"),
$@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include=""Serilog"" Version=""4.0.0"" /></ItemGroup>
</Project>");
            File.WriteAllText(Path.Combine(dir, "C.cs"), $"namespace {name}; public class C {{}}");
        }

        var slnx = Path.Combine(builder.RootDir, "Test.slnx");
        File.WriteAllText(slnx, "<Solution>\r\n  <Project Path=\"A\\A.csproj\" />\r\n  <Project Path=\"B\\B.csproj\" />\r\n  <Project Path=\"C\\C.csproj\" />\r\n</Solution>");

        var result = Parse(await PackageVersionTools.CheckPackageVersions(slnx));

        Assert.Empty(result.Inconsistencies);
        Assert.Equal(1, result.TotalPackages);
    }

    [Fact]
    public async Task PackageVersions_VersionOverride_Tracked()
    {
        using var builder = new TempSolutionBuilder();
        builder.WithDirectoryPackagesProps();
        builder.BuildSlnx();

        var dir = Path.Combine(builder.RootDir, "Lib");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Lib.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" VersionOverride=""14.0.0"" />
  </ItemGroup>
</Project>");
        File.WriteAllText(Path.Combine(dir, "C.cs"), "namespace Lib; public class C {}");

        var slnx = Path.Combine(builder.RootDir, "Test.slnx");
        File.WriteAllText(slnx, "<Solution>\r\n  <Project Path=\"Lib\\Lib.csproj\" />\r\n</Solution>");

        var result = Parse(await PackageVersionTools.CheckPackageVersions(slnx));

        Assert.Single(result.VersionOverrides);
        Assert.Equal("Newtonsoft.Json", result.VersionOverrides[0].PackageId);
        Assert.Equal("14.0.0", result.VersionOverrides[0].OverrideVersion);
    }

    [Fact]
    public async Task PackageVersions_MissingProject_ReportsError()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Good");
        var slnx = builder.BuildSlnx();

        File.WriteAllText(slnx, "<Solution>\r\n  <Project Path=\"Good\\Good.csproj\" />\r\n  <Project Path=\"Gone\\Gone.csproj\" />\r\n</Solution>");

        var result = Parse(await PackageVersionTools.CheckPackageVersions(slnx));

        Assert.Contains(result.Errors, e => e.Contains("Gone"));
    }

    [Fact]
    public async Task PackageVersions_InvalidPath_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => PackageVersionTools.CheckPackageVersions(@"C:\nonexistent.sln"));
    }

    [Fact]
    public async Task PackageVersions_MultipleInconsistencies_AllReported()
    {
        using var builder = new TempSolutionBuilder();
        builder.BuildSlnx();

        var projA = Path.Combine(builder.RootDir, "A");
        var projB = Path.Combine(builder.RootDir, "B");
        Directory.CreateDirectory(projA);
        Directory.CreateDirectory(projB);

        File.WriteAllText(Path.Combine(projA, "A.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Serilog"" Version=""3.0.0"" />
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
  </ItemGroup>
</Project>");
        File.WriteAllText(Path.Combine(projA, "C.cs"), "namespace A; public class C {}");

        File.WriteAllText(Path.Combine(projB, "B.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Serilog"" Version=""4.0.0"" />
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.3"" />
  </ItemGroup>
</Project>");
        File.WriteAllText(Path.Combine(projB, "C.cs"), "namespace B; public class C {}");

        var slnx = Path.Combine(builder.RootDir, "Test.slnx");
        File.WriteAllText(slnx, "<Solution>\r\n  <Project Path=\"A\\A.csproj\" />\r\n  <Project Path=\"B\\B.csproj\" />\r\n</Solution>");

        var result = Parse(await PackageVersionTools.CheckPackageVersions(slnx));

        Assert.Equal(2, result.Inconsistencies.Count);
    }
}
