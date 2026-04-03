using NuGet.Frameworks;

namespace MsBuildGraphMcp.Tests;

/// <summary>
/// NuGet.Frameworks parsing and compatibility edge cases.
/// From NuGet/Home issues #8415, #9734, #13038, #14600, #9927.
/// </summary>
public class TfmParsingTests
{
    private static readonly IFrameworkCompatibilityProvider Compat =
        DefaultCompatibilityProvider.Instance;

    private static NuGetFramework P(string tfm) => NuGetFramework.Parse(tfm);

    // --- Empty/null/malformed input (#8415) ---

    [Fact]
    public void Parse_EmptyString_ReturnsUnsupported()
    {
        var fw = NuGetFramework.Parse("");
        Assert.True(fw.IsUnsupported, "Empty TFM should be Unsupported");
    }

    [Fact]
    public void Parse_UnknownIdentifier_DocumentBehavior()
    {
        // NuGet/Home#8415: Parse("foo,Version=1.0") returns non-Unsupported despite unknown ID
        var fw = NuGetFramework.Parse("foo,Version=1.0");
        // Document actual behavior — this is a known NuGet quirk
        // The framework IS parsed as valid, just with unknown identifier
        Assert.NotNull(fw);
    }

    // --- Platform-specific TFMs (#9734, #14600) ---

    [Fact]
    public void PlatformTfm_WithAndWithoutVersion_DifferentObjects()
    {
        var bare = P("net8.0-windows");
        var versioned = P("net8.0-windows10.0.19041");

        Assert.NotEqual(bare, versioned);
        // Bare has platform version 0.0, versioned has 10.0.19041
        Assert.True(versioned.PlatformVersion > bare.PlatformVersion);
    }

    [Fact]
    public void PlatformTfm_SamePlatform_SameVersion_Compatible()
    {
        Assert.True(Compat.IsCompatible(P("net8.0-windows"), P("net8.0-windows")));
    }

    [Fact]
    public void PlatformSpecific_CanConsume_Generic()
    {
        // net8.0-windows CAN consume net8.0
        Assert.True(Compat.IsCompatible(P("net8.0-windows"), P("net8.0")));
    }

    [Fact]
    public void Generic_CannotConsume_PlatformSpecific()
    {
        // net8.0 CANNOT consume net8.0-windows
        Assert.False(Compat.IsCompatible(P("net8.0"), P("net8.0-windows")));
    }

    // --- Cross-family compatibility (#13038) ---

    [Fact]
    public void Net6_CannotConsume_Net472()
    {
        Assert.False(Compat.IsCompatible(P("net6.0"), P("net472")));
    }

    [Fact]
    public void Net48_CanConsume_Netstandard20()
    {
        Assert.True(Compat.IsCompatible(P("net48"), P("netstandard2.0")));
    }

    [Fact]
    public void Net48_CannotConsume_Netstandard21()
    {
        // .NET Framework 4.8 only supports up to netstandard2.0
        Assert.False(Compat.IsCompatible(P("net48"), P("netstandard2.1")));
    }

    // --- Direction matters ---

    [Theory]
    [InlineData("net9.0", "net8.0", true)]
    [InlineData("net8.0", "net9.0", false)]
    [InlineData("net8.0", "netstandard2.0", true)]
    [InlineData("netstandard2.0", "net8.0", false)]
    [InlineData("net8.0", "net6.0", true)]
    [InlineData("net6.0", "net8.0", false)]
    public void IsCompatible_DirectionMatters(string target, string candidate, bool expected)
    {
        Assert.Equal(expected, Compat.IsCompatible(P(target), P(candidate)));
    }

    // --- FrameworkReducer.GetNearest ---

    [Fact]
    public void GetNearest_Net6_WithOnlyNet472Candidate_ReturnsNull()
    {
        // NuGet/Home#13038: .NET 6 cannot consume .NET Framework 4.7.2
        var reducer = new FrameworkReducer();
        var result = reducer.GetNearest(P("net6.0"), new[] { P("net472") });
        Assert.Null(result);
    }

    [Fact]
    public void GetNearest_Net8_WithMultipleCandidates_PicksBest()
    {
        var reducer = new FrameworkReducer();
        var candidates = new[] { P("netstandard2.0"), P("net6.0"), P("net8.0") };
        var result = reducer.GetNearest(P("net8.0"), candidates);
        Assert.Equal(P("net8.0"), result);
    }

    [Fact]
    public void GetNearest_Net8_WithNetstandard_FallsBack()
    {
        var reducer = new FrameworkReducer();
        var candidates = new[] { P("netstandard2.0"), P("netstandard2.1") };
        var result = reducer.GetNearest(P("net8.0"), candidates);
        Assert.Equal(P("netstandard2.1"), result);
    }
}
