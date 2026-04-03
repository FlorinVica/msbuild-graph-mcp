namespace MsBuildGraphMcp.Models;

public sealed class ConfigurationDiff
{
    public required string ProjectPath { get; init; }
    public required string Config1 { get; init; }
    public required string Config2 { get; init; }
    public required string Platform { get; init; }
    public List<PropertyDifference> PropertyDifferences { get; set; } = [];
    public List<ItemDifference> PackageReferenceDifferences { get; set; } = [];
    public List<ItemDifference> ProjectReferenceDifferences { get; set; } = [];
    public int TotalDifferences => PropertyDifferences.Count +
                                   PackageReferenceDifferences.Count +
                                   ProjectReferenceDifferences.Count;
}

public sealed class PropertyDifference
{
    public required string Name { get; init; }
    public string? Value1 { get; init; }
    public string? Value2 { get; init; }
}

public sealed class ItemDifference
{
    public required string ItemName { get; init; }
    public required string DiffType { get; init; }
    public string? Value1 { get; init; }
    public string? Value2 { get; init; }
}
