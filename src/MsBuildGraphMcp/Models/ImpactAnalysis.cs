namespace MsBuildGraphMcp.Models;

public sealed class ImpactAnalysis
{
    public required string TargetProject { get; init; }
    public required string EntryPath { get; init; }
    public List<string> DirectDependents { get; set; } = [];
    public List<string> TransitiveDependents { get; set; } = [];
    public int TotalImpacted => DirectDependents.Count + TransitiveDependents.Count;
    public string Summary { get; set; } = "";
    public List<string> Errors { get; set; } = [];
}
