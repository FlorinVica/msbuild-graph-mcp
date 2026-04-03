namespace MsBuildGraphMcp.Models;

public sealed class BuildOrderAnalysis
{
    public required string EntryPath { get; init; }
    public List<string> BuildOrder { get; set; } = [];
    public int TotalProjects { get; set; }
    public int CriticalPathLength { get; set; }
    public string? CircularDependencyError { get; set; }
    public List<string> Errors { get; set; } = [];
}
