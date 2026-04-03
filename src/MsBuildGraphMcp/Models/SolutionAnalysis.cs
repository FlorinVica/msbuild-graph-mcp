using System.Text.Json.Serialization;

namespace MsBuildGraphMcp.Models;

public sealed class SolutionAnalysis
{
    public required string SolutionPath { get; init; }
    public string Format { get; set; } = "sln";
    public List<string> Configurations { get; set; } = [];
    public List<ProjectInfo> Projects { get; set; } = [];
    public List<string> ExcludedProjects { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public int ProjectCount => Projects.Count;
}

public sealed class ProjectInfo
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public string? TargetFramework { get; init; }
    public string? TargetFrameworks { get; init; }
    public string? OutputType { get; init; }
    public string? RootNamespace { get; init; }
    public string? IsPackable { get; init; }
    public string? IsTestProject { get; init; }
    public string? Sdk { get; init; }
}
