using AutostartAudit.Core.Model;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Tests;

internal sealed class FakeFolderProbe : IFolderProbe
{
    private readonly Dictionary<string, IReadOnlyList<FsEntry>?> _folders;
    private readonly Dictionary<string, Exception> _failures;

    public FakeFolderProbe(
        Dictionary<string, IReadOnlyList<FsEntry>?>? folders = null,
        Dictionary<string, Exception>? failures = null)
    {
        _folders = folders ?? new();
        _failures = failures ?? new();
    }

    public IReadOnlyList<FsEntry>? TryEnumerate(string folderPath)
    {
        if (_failures.TryGetValue(folderPath, out var ex))
            throw ex;
        return _folders.TryGetValue(folderPath, out var items) ? items : null;
    }
}

public class StartupFolderSourceTests
{
    private static readonly (string Path, string Scope)[] TwoFolders =
    {
        ("/users/me/Startup", "user"),
        ("/ProgramData/Start Menu/Programs/Startup", "machine"),
    };

    [Fact]
    public void PicksUpAcceptedFiles_WithScopeAndPathTarget()
    {
        var probe = new FakeFolderProbe(folders: new()
        {
            ["/users/me/Startup"] = new FsEntry[]
            {
                new("Tool.lnk", false, 1024),
                new("notes.txt", false, 10),          // not accepted
                new("subdir", true, 0),               // directories skipped
            },
            ["/ProgramData/Start Menu/Programs/Startup"] = new FsEntry[]
            {
                new("Updater.exe", false, 4096),
            },
        });

        var result = new StartupFolderSource(probe, TwoFolders).Scan(ScanContext.Default);

        Assert.Equal(2, result.Entries.Count);
        var user = result.Entries.Single(e => e.Scope == "user");
        Assert.Equal("Tool.lnk", user.DisplayName);
        Assert.Equal("/users/me/Startup/Tool.lnk", Assert.Single(user.TargetPaths));
        Assert.Contains("size=1024", user.RawValueSnapshot);
        Assert.Equal(SigningStatus.Unverified, user.Signing);
    }

    [Fact]
    public void DeniedFolder_YieldsDeniedObservation_NotSilentlyEmpty()
    {
        var probe = new FakeFolderProbe(failures: new()
        {
            ["/ProgramData/Start Menu/Programs/Startup"] = new FolderAccessDeniedException("/ProgramData/Start Menu/Programs/Startup", "access denied"),
        });

        var result = new StartupFolderSource(probe, TwoFolders).Scan(ScanContext.Default);

        Assert.Empty(result.Entries);
        Assert.Equal(ObservationHealth.Denied,
            result.Observations.Single(o => o.Id == "/ProgramData/Start Menu/Programs/Startup").Health);
    }

    [Fact]
    public void UnknownFailure_YieldsUnknownObservation()
    {
        var probe = new FakeFolderProbe(failures: new()
        {
            ["/users/me/Startup"] = new FolderUnknownException("/users/me/Startup", "io error"),
        });

        var result = new StartupFolderSource(probe, TwoFolders).Scan(ScanContext.Default);

        Assert.Equal(ObservationHealth.Unknown,
            result.Observations.Single(o => o.Id == "/users/me/Startup").Health);
    }

    [Fact]
    public void AbsentFolder_IsHonestAbsence_ObservationOk()
    {
        var result = new StartupFolderSource(new FakeFolderProbe(), TwoFolders).Scan(ScanContext.Default);

        Assert.Empty(result.Entries);
        Assert.Equal(2, result.Observations.Count);
        Assert.All(result.Observations, o => Assert.Equal(ObservationHealth.Ok, o.Health));
    }

    [Fact]
    public void NoResolvableFolders_IsUnsupported_NotClean()
    {
        var result = new StartupFolderSource(new FakeFolderProbe(), Array.Empty<(string, string)>()).Scan(ScanContext.Default);

        Assert.Equal(SourceCapability.Unsupported, result.ForcedCapability);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public void StableKey_IsDeterministic_AndUsesNormalizedFullPath()
    {
        var probe = new FakeFolderProbe(folders: new()
        {
            ["/users/me/Startup"] = new[] { new FsEntry("Tool.LNK", false, 10) },
        });

        var result = new StartupFolderSource(probe, TwoFolders).Scan(ScanContext.Default);
        var key = result.Entries.Single().StableKey;

        Assert.Equal($"startup-folder|user|/users/me/startup/tool.lnk", key);
    }
}
