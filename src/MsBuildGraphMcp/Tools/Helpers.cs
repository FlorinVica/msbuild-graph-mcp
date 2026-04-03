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

    internal static string ValidateSolutionPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Solution path cannot be empty.");

        var resolved = Path.GetFullPath(input);

        if (resolved.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("UNC paths are not supported.");

        var ext = Path.GetExtension(resolved);
        if (!ValidExtensions.Contains(ext))
            throw new ArgumentException($"Unsupported solution extension: {ext}. Expected .sln, .slnx, or .slnf");

        if (!File.Exists(resolved))
            throw new FileNotFoundException($"Solution not found: {resolved}");

        return resolved;
    }

    internal static string ValidateProjectPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Project path cannot be empty.");

        var resolved = Path.GetFullPath(input);

        if (resolved.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("UNC paths are not supported.");

        var ext = Path.GetExtension(resolved);
        if (!ValidProjectExtensions.Contains(ext))
            throw new ArgumentException($"Unsupported project extension: {ext}");

        if (!File.Exists(resolved))
            throw new FileNotFoundException($"Project not found: {resolved}");

        return resolved;
    }

    internal static string ValidateEntryPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Path cannot be empty.");

        var resolved = Path.GetFullPath(input);

        if (resolved.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("UNC paths are not supported.");

        var ext = Path.GetExtension(resolved);
        if (!ValidExtensions.Contains(ext) && !ValidProjectExtensions.Contains(ext))
            throw new ArgumentException($"Unsupported file extension: {ext}");

        if (!File.Exists(resolved))
            throw new FileNotFoundException($"File not found: {resolved}");

        return resolved;
    }

    /// <summary>
    /// Resolves entry points for ProjectGraph. .slnx files need pre-parsing because
    /// MSBuild's built-in SolutionFile parser (used internally by ProjectGraph) does not
    /// support .slnx format on .NET 8 SDK. We parse with SolutionPersistence and pass
    /// individual project paths as entry points.
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

        // .sln, .slnf, .csproj — ProjectGraph handles these natively
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
