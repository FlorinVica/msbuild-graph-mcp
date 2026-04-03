namespace MsBuildGraphMcp.Tests.Fixtures;

/// <summary>
/// Builds real .sln/.slnx/.csproj files in temp directories for integration testing.
/// Implements IDisposable — cleans up temp files after test.
/// </summary>
public sealed class TempSolutionBuilder : IDisposable
{
    public string RootDir { get; }
    private readonly List<(string name, string tfm, string outputType, List<string> refs)> _projects = [];
    private bool _addDirectoryBuildProps;
    private string? _directoryBuildPropsContent;
    private bool _addDirectoryPackagesProps;

    public TempSolutionBuilder()
    {
        RootDir = Path.Combine(Path.GetTempPath(), $"mcp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(RootDir);
    }

    public TempSolutionBuilder AddProject(string name, string tfm = "net8.0",
        string outputType = "Library", params string[] references)
    {
        _projects.Add((name, tfm, outputType, references.ToList()));
        return this;
    }

    public TempSolutionBuilder AddMultiTargetProject(string name, string tfms,
        string outputType = "Library", params string[] references)
    {
        // tfms like "net8.0;net6.0;netstandard2.0"
        _projects.Add((name, tfms, outputType, references.ToList()));
        return this;
    }

    public TempSolutionBuilder WithDirectoryBuildProps(string? content = null)
    {
        _addDirectoryBuildProps = true;
        _directoryBuildPropsContent = content;
        return this;
    }

    public TempSolutionBuilder WithDirectoryPackagesProps()
    {
        _addDirectoryPackagesProps = true;
        return this;
    }

    /// <summary>Builds a .sln file and returns its path.</summary>
    public string BuildSln(string name = "Test")
    {
        WriteProjects();
        WriteSln(name);
        WriteOptionalFiles();
        return Path.Combine(RootDir, $"{name}.sln");
    }

    /// <summary>Builds a .slnx file and returns its path.</summary>
    public string BuildSlnx(string name = "Test")
    {
        WriteProjects();
        WriteSlnx(name);
        WriteOptionalFiles();
        return Path.Combine(RootDir, $"{name}.slnx");
    }

    /// <summary>Returns path to a specific project .csproj.</summary>
    public string GetProjectPath(string name) =>
        Path.Combine(RootDir, name, $"{name}.csproj");

    private void WriteProjects()
    {
        foreach (var (name, tfm, outputType, refs) in _projects)
        {
            var dir = Path.Combine(RootDir, name);
            Directory.CreateDirectory(dir);

            bool isMultiTarget = tfm.Contains(';');
            string tfmElement = isMultiTarget
                ? $"<TargetFrameworks>{tfm}</TargetFrameworks>"
                : $"<TargetFramework>{tfm}</TargetFramework>";

            var refElements = string.Join("\r\n    ",
                refs.Select(r => $"<ProjectReference Include=\"..\\{r}\\{r}.csproj\" />"));

            var itemGroup = refs.Count > 0
                ? $"\r\n  <ItemGroup>\r\n    {refElements}\r\n  </ItemGroup>"
                : "";

            File.WriteAllText(Path.Combine(dir, $"{name}.csproj"),
$@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>{outputType}</OutputType>
    {tfmElement}
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>{itemGroup}
</Project>
");
            // Write a dummy .cs so it compiles
            File.WriteAllText(Path.Combine(dir, "Class1.cs"),
                $"namespace {name};\r\npublic class Class1 {{ }}\r\n");
        }
    }

    private void WriteSln(string name)
    {
        var lines = new List<string>
        {
            "",
            "Microsoft Visual Studio Solution File, Format Version 12.00",
            "# Visual Studio Version 17",
            "VisualStudioVersion = 17.0.31903.59",
            "MinimumVisualStudioVersion = 10.0.40219.1"
        };

        foreach (var (projName, _, _, _) in _projects)
        {
            var guid = Guid.NewGuid().ToString("B").ToUpper();
            var typeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"; // C#
            lines.Add($"Project(\"{typeGuid}\") = \"{projName}\", \"{projName}\\{projName}.csproj\", \"{guid}\"");
            lines.Add("EndProject");
        }

        lines.Add("Global");
        lines.Add("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
        lines.Add("\t\tDebug|Any CPU = Debug|Any CPU");
        lines.Add("\t\tRelease|Any CPU = Release|Any CPU");
        lines.Add("\tEndGlobalSection");
        lines.Add("EndGlobal");

        File.WriteAllText(Path.Combine(RootDir, $"{name}.sln"), string.Join("\r\n", lines));
    }

    private void WriteSlnx(string name)
    {
        var projects = string.Join("\r\n  ",
            _projects.Select(p => $"<Project Path=\"{p.name}\\{p.name}.csproj\" />"));

        File.WriteAllText(Path.Combine(RootDir, $"{name}.slnx"),
$@"<Solution>
  {projects}
</Solution>
");
    }

    private void WriteOptionalFiles()
    {
        if (_addDirectoryBuildProps)
        {
            var content = _directoryBuildPropsContent ??
@"<Project>
  <PropertyGroup>
    <LangVersion>12</LangVersion>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>";
            File.WriteAllText(Path.Combine(RootDir, "Directory.Build.props"), content);
        }

        if (_addDirectoryPackagesProps)
        {
            File.WriteAllText(Path.Combine(RootDir, "Directory.Packages.props"),
@"<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>");
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(RootDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
