namespace MsBuildGraphMcp.Models;

public sealed class PackageVersionAnalysis
{
    public required string SolutionPath { get; init; }
    public bool CpmEnabled { get; set; }
    public int TotalPackages { get; set; }
    public List<PackageInconsistency> Inconsistencies { get; set; } = [];
    public List<VersionOverrideInfo> VersionOverrides { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}

public sealed class PackageInconsistency
{
    public required string PackageId { get; init; }
    public Dictionary<string, List<string>> VersionToProjects { get; set; } = new();
    public int VersionCount => VersionToProjects.Count;
}

public sealed class VersionOverrideInfo
{
    public required string Project { get; init; }
    public required string PackageId { get; init; }
    public required string OverrideVersion { get; init; }
}
