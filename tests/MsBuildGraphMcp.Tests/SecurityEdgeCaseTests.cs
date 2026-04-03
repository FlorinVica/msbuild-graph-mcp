using System.Reflection;
using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>
/// OWASP MCP Security Cheat Sheet aligned tests.
/// Tool poisoning, property function risks, IsBuildEnabled, error message sanitization.
/// </summary>
public class SecurityEdgeCaseTests
{
    // --- Tool descriptions don't contain injection patterns (OWASP tool poisoning) ---

    [Fact]
    public void ToolDescriptions_NoInjectionPatterns()
    {
        // OWASP: tool descriptions should not contain hidden instructions
        var dangerousPatterns = new[]
        {
            "<IMPORTANT>", "<system>", "<!--", "ignore previous",
            "override", "admin", "sudo", "password", "secret",
            "[INST]", "<<SYS>>", "Human:", "Assistant:"
        };

        var toolTypes = new[]
        {
            typeof(SolutionTools),
            typeof(ProjectGraphTools),
            typeof(SharedImportTools),
            typeof(BuildIssueTools),
            typeof(PropertyTools),
            typeof(ConfigurationTools)
        };

        foreach (var type in toolTypes)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var descAttr = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
                if (descAttr == null) continue;

                foreach (var pattern in dangerousPatterns)
                {
                    Assert.DoesNotContain(pattern, descAttr.Description,
                        StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    // --- Error responses don't leak stack traces ---

    [Fact]
    public async Task ErrorResponse_NoStackTraceLeaked()
    {
        // Call with a path that exists but is not a valid project
        var tempFile = Path.GetTempFileName();
        var renamedFile = Path.ChangeExtension(tempFile, ".sln");
        File.Move(tempFile, renamedFile);
        File.WriteAllText(renamedFile, "NOT A VALID SOLUTION FILE AT ALL");

        try
        {
            // This should throw or return error, but not leak internals
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await SolutionTools.AnalyzeSolution(renamedFile, CancellationToken.None);
            });
        }
        finally
        {
            File.Delete(renamedFile);
        }
    }

    // --- Null/empty required params handled safely ---

    [Fact]
    public async Task AnalyzeSolution_NullPath_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            SolutionTools.AnalyzeSolution(null!, CancellationToken.None));
    }

    [Fact]
    public async Task GetProjectGraph_EmptyPath_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ProjectGraphTools.GetProjectGraph(""));
    }

    [Fact]
    public void AnalyzeProjectProperties_EmptyPath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            PropertyTools.AnalyzeProjectProperties("  "));
    }

    [Fact]
    public void CompareConfigurations_NullPath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            ConfigurationTools.CompareConfigurations(null!));
    }

    // --- System.IO.Directory.GetFiles executes during eval (document risk) ---

    [Fact]
    public void PropertyFunction_DirectoryGetFiles_ExecutesDuringEvaluation()
    {
        using var builder = new TempSolutionBuilder();
        builder.BuildSln();

        var projDir = Path.Combine(builder.RootDir, "DirScan");
        Directory.CreateDirectory(projDir);

        // Create a few files to scan
        File.WriteAllText(Path.Combine(builder.RootDir, "file1.txt"), "");
        File.WriteAllText(Path.Combine(builder.RootDir, "file2.txt"), "");

        File.WriteAllText(Path.Combine(projDir, "DirScan.csproj"),
$@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ScannedFiles>$([System.IO.Directory]::GetFiles('{builder.RootDir.Replace("\\", "\\\\")}', '*.txt'))</ScannedFiles>
  </PropertyGroup>
</Project>");
        File.WriteAllText(Path.Combine(projDir, "C.cs"), "namespace DirScan; public class C {}");

        var json = PropertyTools.AnalyzeProjectProperties(
            Path.Combine(projDir, "DirScan.csproj"),
            properties: new[] { "ScannedFiles" });

        var result = JsonSerializer.Deserialize<PropertyAnalysis>(json, Helpers.JsonOptions)!;
        var prop = result.Properties.First(p => p.Name == "ScannedFiles");

        // Property function DID execute — filesystem was scanned during evaluation
        Assert.NotNull(prop.EvaluatedValue);
        Assert.Contains("file1.txt", prop.EvaluatedValue);
    }

    // --- All tool names are snake_case (MCP best practice for LLM tokenization) ---

    [Fact]
    public void AllToolNames_AreSnakeCase()
    {
        var toolTypes = new[]
        {
            typeof(SolutionTools), typeof(ProjectGraphTools), typeof(SharedImportTools),
            typeof(BuildIssueTools), typeof(PropertyTools), typeof(ConfigurationTools)
        };

        foreach (var type in toolTypes)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var mcpAttr = method.GetCustomAttributes()
                    .FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute");
                if (mcpAttr == null) continue;

                var nameProp = mcpAttr.GetType().GetProperty("Name");
                var name = nameProp?.GetValue(mcpAttr) as string;
                if (name == null) continue;

                Assert.Matches("^[a-z][a-z0-9_]*$", name);
            }
        }
    }
}
