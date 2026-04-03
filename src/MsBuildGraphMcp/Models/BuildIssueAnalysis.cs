namespace MsBuildGraphMcp.Models;

public sealed class BuildIssueAnalysis
{
    public required string EntryPath { get; init; }
    public int TotalIssues => CircularDependencies.Count + FrameworkMismatches.Count +
                              OrphanProjects.Count + PlatformMismatches.Count;
    public List<string> CircularDependencies { get; set; } = [];
    public List<FrameworkMismatch> FrameworkMismatches { get; set; } = [];
    public List<OrphanProject> OrphanProjects { get; set; } = [];
    public List<PlatformMismatch> PlatformMismatches { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}

public sealed class FrameworkMismatch
{
    public required string Project { get; init; }
    public required string ProjectTfm { get; init; }
    public required string Dependency { get; init; }
    public required string DependencyTfm { get; init; }
    public required string Reason { get; init; }
}

public sealed class OrphanProject
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public string? OutputType { get; init; }
    public string? Reason { get; init; }
}

public sealed class PlatformMismatch
{
    public required string Project { get; init; }
    public required string ProjectPlatform { get; init; }
    public required string Dependency { get; init; }
    public required string DependencyPlatform { get; init; }
}
