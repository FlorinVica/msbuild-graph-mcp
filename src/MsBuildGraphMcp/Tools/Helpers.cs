using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Build.Construction;
using Microsoft.Build.Graph;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace MsBuildGraphMcp.Tools;

internal static class Helpers
{
    private static readonly HashSet<string> ValidExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sln", ".slnx", ".slnf"
    };

    private static readonly HashSet<string> ValidProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".vbproj", ".fsproj", ".vcxproj", ".esproj", ".wixproj", ".sqlproj"
    };

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private const int MaxPathLength = 500;

    /// <summary>Default timeout for graph construction (configurable via MSBUILD_MCP_TIMEOUT_SECONDS).</summary>
    internal static TimeSpan GraphTimeout { get; } = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("MSBUILD_MCP_TIMEOUT_SECONDS"), out var secs) && secs > 0
            ? secs : 120);

    internal static string ValidateSolutionPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Solution path cannot be empty.");

        if (input.Length > MaxPathLength)
            throw new ArgumentException($"Path exceeds maximum length ({MaxPathLength} chars).");

        var resolved = Path.GetFullPath(input);

        if (resolved.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("UNC paths are not supported.");

        var ext = Path.GetExtension(resolved);
        if (!ValidExtensions.Contains(ext))
            throw new ArgumentException($"Unsupported solution extension: {ext}. Expected .sln, .slnx, or .slnf");

        if (!File.Exists(resolved))
            throw new FileNotFoundException($"Solution not found: {resolved}");

        // Check for symlinks/junctions (potential path traversal)
        var fi = new FileInfo(resolved);
        if (fi.LinkTarget != null || (fi.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Symbolic links and junctions are not supported for security reasons.");

        // Check allowed directories
        var pathError = SecurityScanner.CheckAllowedPath(resolved);
        if (pathError != null)
            throw new ArgumentException(pathError);

        return resolved;
    }

    internal static string ValidateProjectPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Project path cannot be empty.");

        if (input.Length > MaxPathLength)
            throw new ArgumentException($"Path exceeds maximum length ({MaxPathLength} chars).");

        var resolved = Path.GetFullPath(input);

        if (resolved.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("UNC paths are not supported.");

        var ext = Path.GetExtension(resolved);
        if (!ValidProjectExtensions.Contains(ext))
            throw new ArgumentException($"Unsupported project extension: {ext}");

        if (!File.Exists(resolved))
            throw new FileNotFoundException($"Project not found: {resolved}");

        var fi = new FileInfo(resolved);
        if (fi.LinkTarget != null || (fi.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Symbolic links and junctions are not supported for security reasons.");

        var pathError = SecurityScanner.CheckAllowedPath(resolved);
        if (pathError != null)
            throw new ArgumentException(pathError);

        return resolved;
    }

    internal static string ValidateEntryPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Path cannot be empty.");

        if (input.Length > MaxPathLength)
            throw new ArgumentException($"Path exceeds maximum length ({MaxPathLength} chars).");

        var resolved = Path.GetFullPath(input);

        if (resolved.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("UNC paths are not supported.");

        var ext = Path.GetExtension(resolved);
        if (!ValidExtensions.Contains(ext) && !ValidProjectExtensions.Contains(ext))
            throw new ArgumentException($"Unsupported file extension: {ext}");

        if (!File.Exists(resolved))
            throw new FileNotFoundException($"File not found: {resolved}");

        var fi = new FileInfo(resolved);
        if (fi.LinkTarget != null || (fi.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Symbolic links and junctions are not supported for security reasons.");

        var pathError = SecurityScanner.CheckAllowedPath(resolved);
        if (pathError != null)
            throw new ArgumentException(pathError);

        return resolved;
    }

    /// <summary>
    /// Reads a .slnf file and returns absolute project paths. Handles the case where
    /// the .slnf references a .slnx solution (which SolutionFile.Parse doesn't support
    /// on .NET 8 SDK). Project paths in .slnf are relative to the referenced solution.
    /// Returns null if the .slnf does not reference a .slnx (caller should use SolutionFile.Parse).
    /// </summary>
    internal static async Task<List<string>?> TryGetSlnfProjectPathsAsync(string slnfPath, CancellationToken ct)
    {
        var slnfDir = Path.GetDirectoryName(slnfPath)!;
        var json = await File.ReadAllTextAsync(slnfPath, ct);
        using var doc = JsonDocument.Parse(json);
        var solution = doc.RootElement.GetProperty("solution");
        var solutionRelPath = solution.GetProperty("path").GetString()!;

        if (!solutionRelPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            return null;

        var solutionAbsPath = Path.GetFullPath(Path.Combine(slnfDir, solutionRelPath));
        var solutionDir = Path.GetDirectoryName(solutionAbsPath)!;

        return solution.GetProperty("projects").EnumerateArray()
            .Select(p => Path.GetFullPath(Path.Combine(solutionDir, p.GetString()!)))
            .ToList();
    }

    /// <summary>
    /// Resolves entry points for ProjectGraph. .slnx files need pre-parsing because
    /// MSBuild's built-in SolutionFile parser (used internally by ProjectGraph) does not
    /// support .slnx format on .NET 8 SDK. We parse with SolutionPersistence and pass
    /// individual project paths as entry points.
    /// Also handles .slnf files that reference .slnx solutions.
    /// </summary>
    internal static async Task<ProjectGraphEntryPoint[]> ResolveEntryPointsAsync(
        string entryPath,
        Dictionary<string, string> globalProps,
        CancellationToken ct)
    {
        string ext = Path.GetExtension(entryPath).ToLowerInvariant();

        if (ext == ".slnx")
        {
            var solutionDir = Path.GetDirectoryName(entryPath)!;
            var serializer = SolutionSerializers.SlnXml;
            var model = await serializer.OpenAsync(entryPath, ct);
            return model.SolutionProjects
                .Select(p =>
                {
                    var absPath = Path.IsPathRooted(p.FilePath)
                        ? p.FilePath
                        : Path.GetFullPath(Path.Combine(solutionDir, p.FilePath));
                    return new ProjectGraphEntryPoint(absPath, globalProps);
                })
                .ToArray();
        }

        if (ext == ".slnf")
        {
            var slnfPaths = await TryGetSlnfProjectPathsAsync(entryPath, ct);
            if (slnfPaths != null)
                return slnfPaths.Select(p => new ProjectGraphEntryPoint(p, globalProps)).ToArray();
        }

        // .sln, .slnf (referencing .sln), .csproj — ProjectGraph handles these natively
        return [new ProjectGraphEntryPoint(entryPath, globalProps)];
    }

    internal static string RelativeTo(string fullPath, string basePath)
    {
        try
        {
            return Path.GetRelativePath(basePath, fullPath);
        }
        catch
        {
            return fullPath;
        }
    }
}
