using System.Text.RegularExpressions;

namespace MsBuildGraphMcp.Tools;

/// <summary>
/// Pre-evaluation security scanner. Scans project XML for dangerous MSBuild
/// property functions BEFORE MSBuild evaluation executes them.
/// </summary>
internal static partial class SecurityScanner
{
    // Dangerous property function patterns that execute during evaluation
    private static readonly Regex DangerousPropertyFunctions = CreateDangerousRegex();

    [GeneratedRegex(
        @"\$\(\[\s*(?:" +
            @"System\.IO\.File\b|" +
            @"System\.IO\.Directory\b|" +
            @"System\.Environment\b|" +
            @"System\.Diagnostics\b|" +
            @"System\.Net\b|" +
            @"System\.Reflection\b|" +
            @"System\.Security\b|" +
            @"System\.Threading\.Thread\b" +
        @")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex CreateDangerousRegex();

    /// <summary>
    /// Scans a project file for dangerous property functions.
    /// Returns list of warnings (not blocking — just informational).
    /// </summary>
    internal static List<string> ScanProjectFile(string projectPath)
    {
        var warnings = new List<string>();

        try
        {
            string content = File.ReadAllText(projectPath);

            var matches = DangerousPropertyFunctions.Matches(content);
            foreach (Match match in matches)
            {
                // Find approximate line number
                int lineNum = content[..match.Index].Count(c => c == '\n') + 1;
                warnings.Add($"[SECURITY] {Path.GetFileName(projectPath)}:{lineNum} — " +
                    $"Property function detected: {match.Value[..Math.Min(80, match.Value.Length)]}");
            }

            // Check for CodeTaskFactory (inline code execution)
            if (content.Contains("CodeTaskFactory", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"[SECURITY] {Path.GetFileName(projectPath)} — " +
                    "Contains CodeTaskFactory (inline code compilation). " +
                    "Code will NOT execute (IsBuildEnabled=false) but indicates untrusted content.");
            }

            // Check for Exec tasks in targets
            if (Regex.IsMatch(content, @"<Exec\s+Command", RegexOptions.IgnoreCase))
            {
                warnings.Add($"[SECURITY] {Path.GetFileName(projectPath)} — " +
                    "Contains <Exec> tasks. Commands will NOT execute (IsBuildEnabled=false).");
            }
        }
        catch
        {
            // Can't scan — not a blocking error
        }

        return warnings;
    }

    // --- Allowed directories ---

    private static readonly Lazy<HashSet<string>?> AllowedPaths = new(() =>
    {
        var envVar = Environment.GetEnvironmentVariable("MSBUILD_MCP_ALLOWED_PATHS");
        if (string.IsNullOrWhiteSpace(envVar))
            return null; // No restriction

        return envVar.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    });

    /// <summary>
    /// Checks if a path is within the allowed directories.
    /// Returns null if allowed, or error message if blocked.
    /// When MSBUILD_MCP_ALLOWED_PATHS is not set, all paths are allowed.
    /// </summary>
    internal static string? CheckAllowedPath(string resolvedPath)
    {
        var allowed = AllowedPaths.Value;
        if (allowed == null)
            return null; // No restriction configured

        var dir = Path.GetDirectoryName(resolvedPath) ?? resolvedPath;

        foreach (var allowedDir in allowed)
        {
            if (dir.StartsWith(allowedDir, StringComparison.OrdinalIgnoreCase))
                return null; // Within allowed directory
        }

        return $"Path is outside allowed directories. Set MSBUILD_MCP_ALLOWED_PATHS to include this location. " +
               $"Allowed: {string.Join("; ", allowed)}";
    }

    // --- Error message sanitization ---

    /// <summary>
    /// Strips user profile paths from error messages to prevent information disclosure.
    /// </summary>
    internal static string SanitizeErrorMessage(string message)
    {
        // Replace user profile paths with ~
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
            message = message.Replace(userProfile, "~", StringComparison.OrdinalIgnoreCase);

        // Strip stack traces (anything starting with "   at ")
        var lines = message.Split('\n');
        var filtered = lines.Where(l => !l.TrimStart().StartsWith("at ")).ToArray();
        return string.Join('\n', filtered).Trim();
    }
}
