using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>
/// Security tests per OWASP MCP Security Cheat Sheet and MSBuild security docs.
/// Property functions, environment variable exposure, IsBuildEnabled, path validation.
/// </summary>
public class SecurityTests
{
    // --- MSBUILDENABLEALLPROPERTYFUNCTIONS should NOT be set ---

    [Fact]
    public void MSBUILDENABLEALLPROPERTYFUNCTIONS_NotSet()
    {
        // CVE-2025-21172: this env var enables arbitrary code execution during evaluation
        var val = Environment.GetEnvironmentVariable("MSBUILDENABLEALLPROPERTYFUNCTIONS");
        Assert.True(val == null || val == "0",
            "MSBUILDENABLEALLPROPERTYFUNCTIONS must not be set — enables arbitrary code execution during MSBuild evaluation");
    }

    // --- Path validation: reject traversal attempts ---

    [Fact]
    public void PathTraversal_ResolvedButFileNotFound()
    {
        // Path traversal with .. resolves via Path.GetFullPath, then fails on File.Exists
        Assert.ThrowsAny<Exception>(() =>
            Helpers.ValidateSolutionPath(@"C:\src\..\..\Windows\System32\config.sln"));
    }

    [Fact]
    public void UncPath_RejectedForAllValidators()
    {
        Assert.Throws<ArgumentException>(() => Helpers.ValidateSolutionPath(@"\\evil\share\malicious.sln"));
        Assert.Throws<ArgumentException>(() => Helpers.ValidateProjectPath(@"\\evil\share\malicious.csproj"));
        Assert.Throws<ArgumentException>(() => Helpers.ValidateEntryPath(@"\\evil\share\malicious.sln"));
    }

    // --- Property function: File.ReadAllText executes during evaluation ---

    [Fact]
    public void PropertyFunction_FileRead_ExecutesDuringEvaluation()
    {
        // This is a known MSBuild behavior: property functions execute at evaluation time.
        // Our server sets IsBuildEnabled=false, but that doesn't prevent property function evaluation.
        // This test documents the behavior.
        using var builder = new TempSolutionBuilder();
        builder.BuildSln();

        var projDir = Path.Combine(builder.RootDir, "Risky");
        Directory.CreateDirectory(projDir);

        // Write a secret file that the property function will try to read
        var secretFile = Path.Combine(builder.RootDir, "secret.txt");
        File.WriteAllText(secretFile, "TOP_SECRET_VALUE");

        File.WriteAllText(Path.Combine(projDir, "Risky.csproj"),
$@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <SecretData>$([System.IO.File]::ReadAllText('{secretFile.Replace("\\", "\\\\")}'))</SecretData>
  </PropertyGroup>
</Project>");
        File.WriteAllText(Path.Combine(projDir, "C.cs"), "namespace Risky; public class C {}");

        // Property function WILL execute during evaluation (this is by design in MSBuild)
        var json = PropertyTools.AnalyzeProjectProperties(
            Path.Combine(projDir, "Risky.csproj"),
            properties: new[] { "SecretData" });

        var result = JsonSerializer.Deserialize<PropertyAnalysis>(json, Helpers.JsonOptions)!;
        var prop = result.Properties.FirstOrDefault(p => p.Name == "SecretData");

        // Documenting the risk: property functions DO execute and CAN read files
        Assert.NotNull(prop);
        Assert.Equal("TOP_SECRET_VALUE", prop.EvaluatedValue);
        // THIS IS THE SECURITY RISK — evaluate only trusted projects!
    }

    // --- Environment variable exposure through property functions ---

    [Fact]
    public void PropertyFunction_EnvVar_CanReadEnvironment()
    {
        using var builder = new TempSolutionBuilder();
        builder.BuildSln();

        var projDir = Path.Combine(builder.RootDir, "EnvLeak");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "EnvLeak.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <LeakedPath>$([System.Environment]::GetEnvironmentVariable('PATH'))</LeakedPath>
  </PropertyGroup>
</Project>");
        File.WriteAllText(Path.Combine(projDir, "C.cs"), "namespace EnvLeak; public class C {}");

        var json = PropertyTools.AnalyzeProjectProperties(
            Path.Combine(projDir, "EnvLeak.csproj"),
            properties: new[] { "LeakedPath" });

        var result = JsonSerializer.Deserialize<PropertyAnalysis>(json, Helpers.JsonOptions)!;
        var prop = result.Properties.First(p => p.Name == "LeakedPath");

        // Environment variables ARE accessible during evaluation
        Assert.NotNull(prop.EvaluatedValue);
        Assert.True(prop.EvaluatedValue!.Length > 10, "PATH should be non-trivial");
    }

    // --- Extension whitelist enforcement ---

    [Theory]
    [InlineData(@"C:\foo\bar.exe")]
    [InlineData(@"C:\foo\bar.dll")]
    [InlineData(@"C:\foo\bar.bat")]
    [InlineData(@"C:\foo\bar.ps1")]
    [InlineData(@"C:\foo\bar.cmd")]
    [InlineData(@"C:\foo\bar.msi")]
    public void DangerousExtensions_Rejected(string path)
    {
        Assert.ThrowsAny<ArgumentException>(() => Helpers.ValidateEntryPath(path));
        Assert.ThrowsAny<ArgumentException>(() => Helpers.ValidateProjectPath(path));
    }

    [Theory]
    [InlineData(@"C:\foo\bar.xml")]
    [InlineData(@"C:\foo\bar.json")]
    [InlineData(@"C:\foo\bar.yaml")]
    public void NonProjectExtensions_Rejected(string path)
    {
        Assert.ThrowsAny<ArgumentException>(() => Helpers.ValidateProjectPath(path));
    }
}
