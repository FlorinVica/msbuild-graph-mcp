using System.ComponentModel;
using System.Text.Json;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using ModelContextProtocol.Server;
using MsBuildGraphMcp.Models;
using static MsBuildGraphMcp.Tools.Helpers;

namespace MsBuildGraphMcp.Tools;

[McpServerToolType]
public static class SolutionTools
{
    [McpServerTool(Name = "analyze_solution", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Parses a .sln, .slnx, or .slnf solution file and returns the complete project list " +
        "with target frameworks, output types, test/packable flags, solution configurations, " +
        "and projects excluded from specific build configurations. " +
        "Input must be an absolute path to an existing solution file. " +
        "Example: analyze_solution(\"C:\\src\\MyApp.sln\")")]
    public static async Task<string> AnalyzeSolution(
        [Description("Absolute path to .sln, .slnx, or .slnf file")] string solutionPath,
        CancellationToken cancellationToken)
    {
        solutionPath = ValidateSolutionPath(solutionPath);
        var solutionDir = Path.GetDirectoryName(solutionPath)!;
        var result = new SolutionAnalysis { SolutionPath = solutionPath };

        string ext = Path.GetExtension(solutionPath).ToLowerInvariant();
        var projectEntries = new List<(string Name, string AbsPath)>();

        if (ext == ".slnx")
        {
            result.Format = "slnx";
            var serializer = SolutionSerializers.SlnXml;
            var model = await serializer.OpenAsync(solutionPath, cancellationToken);
            foreach (var p in model.SolutionProjects)
            {
                var absPath = Path.IsPathRooted(p.FilePath)
                    ? p.FilePath
                    : Path.GetFullPath(Path.Combine(solutionDir, p.FilePath));
                projectEntries.Add((p.DisplayName ?? Path.GetFileNameWithoutExtension(p.FilePath), absPath));
            }
        }
        else
        {
            result.Format = ext == ".slnf" ? "slnf" : "sln";
            var sln = SolutionFile.Parse(solutionPath);
            result.Configurations = sln.SolutionConfigurations
                .Select(c => c.FullName).ToList();

            foreach (var proj in sln.ProjectsInOrder)
            {
                if (proj.ProjectType == SolutionProjectType.SolutionFolder)
                    continue;
                if (proj.ProjectType != SolutionProjectType.KnownToBeMSBuildFormat)
                    continue;

                projectEntries.Add((proj.ProjectName, proj.AbsolutePath));

                // Detect excluded configurations
                foreach (var config in sln.SolutionConfigurations)
                {
                    if (proj.ProjectConfigurations.TryGetValue(config.FullName, out var pc)
                        && !pc.IncludeInBuild)
                    {
                        result.ExcludedProjects.Add(
                            $"{proj.ProjectName} excluded from {config.FullName}");
                    }
                }
            }
        }

        // Evaluate each project for properties
        using var collection = new ProjectCollection();
        collection.IsBuildEnabled = false;

        foreach (var (name, absPath) in projectEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(absPath))
            {
                result.Errors.Add($"{name}: file not found at {absPath}");
                continue;
            }

            // Pre-scan for dangerous property functions
            var secWarnings = SecurityScanner.ScanProjectFile(absPath);
            foreach (var w in secWarnings)
                Console.Error.WriteLine(w);

            Project? project = null;
            try
            {
                project = collection.LoadProject(absPath);
                result.Projects.Add(new ProjectInfo
                {
                    Name = name,
                    Path = absPath,
                    TargetFramework = NullIfEmpty(project.GetPropertyValue("TargetFramework")),
                    TargetFrameworks = NullIfEmpty(project.GetPropertyValue("TargetFrameworks")),
                    OutputType = NullIfEmpty(project.GetPropertyValue("OutputType")),
                    RootNamespace = NullIfEmpty(project.GetPropertyValue("RootNamespace")),
                    IsPackable = NullIfEmpty(project.GetPropertyValue("IsPackable")),
                    IsTestProject = NullIfEmpty(project.GetPropertyValue("IsTestProject")),
                    Sdk = NullIfEmpty(project.Xml.Sdk)
                });
            }
            catch (Exception ex) when (ex is InvalidProjectFileException or InvalidOperationException)
            {
                result.Errors.Add($"{name}: {ex.Message}");
            }
            finally
            {
                if (project != null) collection.UnloadProject(project);
            }
        }

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
