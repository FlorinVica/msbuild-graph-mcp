using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

public class HelpersTests
{
    // --- ValidateSolutionPath ---

    [Fact]
    public void ValidateSolutionPath_Null_Throws()
    {
        Assert.Throws<ArgumentException>(() => Helpers.ValidateSolutionPath(null!));
    }

    [Fact]
    public void ValidateSolutionPath_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => Helpers.ValidateSolutionPath(""));
    }

    [Fact]
    public void ValidateSolutionPath_Whitespace_Throws()
    {
        Assert.Throws<ArgumentException>(() => Helpers.ValidateSolutionPath("   "));
    }

    [Fact]
    public void ValidateSolutionPath_UncPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => Helpers.ValidateSolutionPath(@"\\server\share\foo.sln"));
    }

    [Theory]
    [InlineData(@"C:\foo\bar.txt")]
    [InlineData(@"C:\foo\bar.csproj")]
    [InlineData(@"C:\foo\bar.xml")]
    [InlineData(@"C:\foo\bar")]
    public void ValidateSolutionPath_WrongExtension_Throws(string path)
    {
        Assert.Throws<ArgumentException>(() => Helpers.ValidateSolutionPath(path));
    }

    [Fact]
    public void ValidateSolutionPath_FileNotFound_Throws()
    {
        Assert.Throws<FileNotFoundException>(() =>
            Helpers.ValidateSolutionPath(@"C:\nonexistent\path\to\test.sln"));
    }

    [Fact]
    public void ValidateSolutionPath_ValidSln_ReturnsFullPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"helpers-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var slnPath = Path.Combine(dir, "Test.sln");
        File.WriteAllText(slnPath, "");
        try
        {
            var result = Helpers.ValidateSolutionPath(slnPath);
            Assert.Equal(Path.GetFullPath(slnPath), result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ValidateSolutionPath_ValidSlnx_ReturnsFullPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"helpers-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var slnxPath = Path.Combine(dir, "Test.slnx");
        File.WriteAllText(slnxPath, "");
        try
        {
            var result = Helpers.ValidateSolutionPath(slnxPath);
            Assert.Equal(Path.GetFullPath(slnxPath), result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ValidateSolutionPath_ValidSlnf_ReturnsFullPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"helpers-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var slnfPath = Path.Combine(dir, "Test.slnf");
        File.WriteAllText(slnfPath, "");
        try
        {
            var result = Helpers.ValidateSolutionPath(slnfPath);
            Assert.Equal(Path.GetFullPath(slnfPath), result);
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- ValidateProjectPath ---

    [Fact]
    public void ValidateProjectPath_Null_Throws()
    {
        Assert.Throws<ArgumentException>(() => Helpers.ValidateProjectPath(null!));
    }

    [Fact]
    public void ValidateProjectPath_UncPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => Helpers.ValidateProjectPath(@"\\server\share\foo.csproj"));
    }

    [Theory]
    [InlineData(@"C:\foo\bar.sln")]
    [InlineData(@"C:\foo\bar.txt")]
    [InlineData(@"C:\foo\bar.xml")]
    public void ValidateProjectPath_WrongExtension_Throws(string path)
    {
        Assert.Throws<ArgumentException>(() => Helpers.ValidateProjectPath(path));
    }

    [Theory]
    [InlineData(".csproj")]
    [InlineData(".vbproj")]
    [InlineData(".fsproj")]
    [InlineData(".vcxproj")]
    public void ValidateProjectPath_ValidExtensions_Accepted(string ext)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"helpers-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var projPath = Path.Combine(dir, $"Test{ext}");
        File.WriteAllText(projPath, "");
        try
        {
            var result = Helpers.ValidateProjectPath(projPath);
            Assert.Equal(Path.GetFullPath(projPath), result);
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- ValidateEntryPath ---

    [Fact]
    public void ValidateEntryPath_AcceptsSln()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"helpers-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, "Test.sln");
        File.WriteAllText(p, "");
        try { Assert.Equal(Path.GetFullPath(p), Helpers.ValidateEntryPath(p)); }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ValidateEntryPath_AcceptsCsproj()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"helpers-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, "Test.csproj");
        File.WriteAllText(p, "");
        try { Assert.Equal(Path.GetFullPath(p), Helpers.ValidateEntryPath(p)); }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ValidateEntryPath_RejectsTxt()
    {
        Assert.Throws<ArgumentException>(() => Helpers.ValidateEntryPath(@"C:\foo\bar.txt"));
    }

    // --- RelativeTo ---

    [Fact]
    public void RelativeTo_SameDrive_ReturnsRelative()
    {
        var result = Helpers.RelativeTo(@"C:\src\proj\A.csproj", @"C:\src");
        Assert.Equal(@"proj\A.csproj", result);
    }

    [Fact]
    public void RelativeTo_SamePath_ReturnsDot()
    {
        var result = Helpers.RelativeTo(@"C:\src\A.csproj", @"C:\src");
        Assert.Equal("A.csproj", result);
    }
}
