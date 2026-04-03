using System.ComponentModel;
using System.Text.Json;
using Microsoft.Build.Evaluation;
using ModelContextProtocol.Server;
using MsBuildGraphMcp.Models;
using static MsBuildGraphMcp.Tools.Helpers;

namespace MsBuildGraphMcp.Tools;

[McpServerToolType]
public static class PropertyTools
{
    private static readonly string[] DefaultProperties =
    [
        "TargetFramework", "TargetFrameworks", "OutputType", "AssemblyName",
        "RootNamespace", "LangVersion", "Nullable", "ImplicitUsings",
        "DefineConstants", "Optimize", "DebugType", "DebugSymbols",
        "OutputPath", "IntermediateOutputPath", "TreatWarningsAsErrors",
        "IsPackable", "IsTestProject", "GenerateDocumentationFile",
        "ManagePackageVersionsCentrally", "CentralPackageTransitivePinningEnabled"
    ];

    [McpServerTool(Name = "analyze_project_properties", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Evaluates MSBuild properties for a single project file with full source tracking — " +
        "shows which file defined each property, whether it was imported, its line number, " +
        "and any previous overridden values. Also lists the complete import chain. " +
        "Without specific property names, returns key build properties. " +
        "Example: analyze_project_properties(\"C:\\src\\MyLib\\MyLib.csproj\") " +
        "Example with filter: analyze_project_properties(\"...\", properties: [\"LangVersion\", \"Nullable\"])")]
    public static string AnalyzeProjectProperties(
        [Description("Absolute path to .csproj, .vbproj, .fsproj, or .vcxproj file")] string projectPath,
        [Description("Specific property names to inspect. If omitted, returns common build properties.")] string[]? properties = null,
        CancellationToken cancellationToken = default)
    {
        projectPath = ValidateProjectPath(projectPath);

        var result = new PropertyAnalysis { ProjectPath = projectPath };

        using var collection = new ProjectCollection();
        collection.IsBuildEnabled = false;

        var project = new Project(
            projectPath, null, null, collection,
            ProjectLoadSettings.IgnoreMissingImports | ProjectLoadSettings.IgnoreInvalidImports);

        // Determine which properties to report (filter out empty/whitespace entries)
        var propertyNames = properties is { Length: > 0 }
            ? properties.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray()
            : DefaultProperties;

        if (propertyNames.Length == 0)
            propertyNames = DefaultProperties;

        foreach (string propName in propertyNames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var prop = project.GetProperty(propName);
            if (prop == null)
            {
                // Property not set — still report it as absent if explicitly requested
                if (properties is { Length: > 0 })
                {
                    result.Properties.Add(new PropertyDetail
                    {
                        Name = propName,
                        EvaluatedValue = null,
                        UnevaluatedValue = null,
                        SourceFile = null,
                        IsImported = false,
                        IsGlobal = false,
                        IsEnvironment = false,
                        IsReserved = false
                    });
                }
                continue;
            }

            string? sourceFile = null;
            int? sourceLine = null;

            if (prop.Xml != null)
            {
                sourceFile = prop.Xml.ContainingProject?.FullPath;
                sourceLine = prop.Xml.Location?.Line;
            }

            string? previousValue = null;
            if (prop.Predecessor != null)
                previousValue = prop.Predecessor.EvaluatedValue;

            result.Properties.Add(new PropertyDetail
            {
                Name = propName,
                EvaluatedValue = string.IsNullOrEmpty(prop.EvaluatedValue) ? null : prop.EvaluatedValue,
                UnevaluatedValue = string.IsNullOrEmpty(prop.UnevaluatedValue) ? null : prop.UnevaluatedValue,
                SourceFile = sourceFile,
                SourceLine = sourceLine,
                IsImported = prop.IsImported,
                IsGlobal = prop.IsGlobalProperty,
                IsEnvironment = prop.IsEnvironmentProperty,
                IsReserved = prop.IsReservedProperty,
                PreviousValue = previousValue
            });
        }

        // Import chain
        foreach (var import in project.Imports)
        {
            string importedFile = import.ImportedProject.FullPath;
            string importedBy = import.ImportingElement.ContainingProject.FullPath;

            result.ImportChain.Add(new ImportInfo
            {
                ImportedFile = importedFile,
                ImportedBy = importedBy,
                IsSdkImport = import.ImportingElement.Sdk != null
            });
        }

        collection.UnloadProject(project);
        return JsonSerializer.Serialize(result, JsonOptions);
    }
}
