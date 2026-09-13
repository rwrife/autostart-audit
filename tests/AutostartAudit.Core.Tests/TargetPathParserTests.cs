using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Tests;

public class TargetPathParserTests
{
    [Theory]
    [InlineData(@"""C:\Program Files\App\app.exe"" /min", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Tools\tool.exe --startup", @"C:\Tools\tool.exe")]
    [InlineData(@"C:\Tools\helper.exe arg /flag", @"C:\Tools\helper.exe")]
    [InlineData(@"notepad", "notepad")]
    [InlineData(@"  spaced.exe  ", "spaced.exe")]
    [InlineData(@"""D:\x\run.vbs""", @"D:\x\run.vbs")]
    public void ParsesExpectedPath(string raw, string expected)
    {
        Assert.Equal(new[] { expected }, TargetPathParser.Parse(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyValue_YieldsNoPaths_NotFabricatedPath(string raw)
    {
        Assert.Empty(TargetPathParser.Parse(raw));
    }

    [Fact]
    public void UnterminatedQuote_KeepsQuotedFragment()
    {
        Assert.Equal(new[] { @"C:\broken\app.exe" }, TargetPathParser.Parse(@"""C:\broken\app.exe"));
    }

    [Fact]
    public void DoubledQuotesInsidePath_Unescape()
    {
        // Raw: "C:\we""ird\app.exe"  ->  path: C:\we"ird\app.exe
        var raw = "\"C:\\we\"\"ird\\app.exe\"";
        Assert.Equal(new[] { "C:\\we\"ird\\app.exe" }, TargetPathParser.Parse(raw));
    }
}

public class StableKeyTests
{
    [Theory]
    [InlineData("Run-Key | User |  HKCU\\Run\\MyApp ", "run-key | user | hkcu\\run\\myapp")]
    [InlineData("run-key | user | hkcu\\run\\myapp", "run-key | user | hkcu\\run\\myapp")]
    [InlineData("RUN-KEY | USER | HKCU\\RUN\\MYAPP", "run-key | user | hkcu\\run\\myapp")]
    public void Normalization_IsDeterministic_CaseAndWhitespaceInvariant(string input, string expected)
    {
        // Whitespace runs collapse to one space but are never removed —
        // real paths legitimately contain single spaces ("Program Files").
        Assert.Equal(expected, StableKey.Normalize(input));
    }

    [Fact]
    public void Build_EmbedsKindScopeAndId()
    {
        Assert.Equal("run-key|user|a", StableKey.Build("run-key", "user", "A"));
    }
}
