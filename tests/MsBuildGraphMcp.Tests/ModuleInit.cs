using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace MsBuildGraphMcp.Tests;

/// <summary>
/// MSBuildLocator must be registered once before any Microsoft.Build type is used.
/// Module initializer runs before any test method.
/// </summary>
internal static class ModuleInit
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            var instance = MSBuildLocator.QueryVisualStudioInstances()
                .OrderByDescending(i => i.Version)
                .FirstOrDefault();

            if (instance != null)
                MSBuildLocator.RegisterInstance(instance);
            else
                MSBuildLocator.RegisterDefaults();
        }
    }
}
