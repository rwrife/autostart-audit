using AutostartAudit.Core.Model;
using AutostartAudit.Core.Scan;
using AutostartAudit.Core.Snapshot;

namespace AutostartAudit.Core.Tests;

internal static class SnapshotTestFixtures
{
    internal static AutoStartEntry Entry(
        string sourceKind,
        string scope,
        string nativeKey,
        string displayName,
        string[] targets,
        SigningStatus signing = SigningStatus.Unverified,
        bool? enabled = null) => new()
        {
            SourceKind = sourceKind,
            Scope = scope,
            StableKey = StableKey.BuildWithTargets(sourceKind, scope, nativeKey, targets),
            NativeKey = StableKey.Normalize(nativeKey),
            DisplayName = displayName,
            TargetPaths = targets,
            RawValueSnapshot = string.Join(" ", targets),
            Enabled = enabled,
            Signing = signing,
            Observation = ObservationHealth.Ok,
        };

    internal static ScanDocument Doc(params AutoStartEntry[] entries) => new()
    {
        Entries = entries,
        Sources = new[] { new SourceReport { SourceKind = "run-key", Capability = SourceCapability.Scanned } },
        ScanComplete = true,
    };
}

public class DiffEngineClassificationTests
{
    private static AutoStartEntry Base() =>
        SnapshotTestFixtures.Entry("run-key", "user", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run\App", "App",
            new[] { @"C:\Apps\app.exe" });

    [Fact]
    public void IdenticalScans_AllUnchanged_NoAddRemoveChange()
    {
        var diff = DiffEngine.Compare(SnapshotTestFixtures.Doc(Base()), SnapshotTestFixtures.Doc(Base()));
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Changed);
        Assert.Single(diff.Unchanged);
    }

    [Fact]
    public void NewEntry_IsAdded_WithAfterEvidenceOnly()
    {
        var before = SnapshotTestFixtures.Doc();
        var after = SnapshotTestFixtures.Doc(Base());
        var diff = DiffEngine.Compare(before, after);
        var added = Assert.Single(diff.Added);
        Assert.Null(added.DisplayNameBefore);
        Assert.Equal("App", added.DisplayNameAfter);
        Assert.Equal(new[] { @"c:\apps\app.exe" }, added.TargetPathsAfter);
        Assert.Equal(SigningStatus.Unverified, added.SigningAfter);
        Assert.Empty(added.Reasons);
    }

    [Fact]
    public void GoneEntry_IsRemoved_WithBeforeEvidenceOnly()
    {
        var diff = DiffEngine.Compare(SnapshotTestFixtures.Doc(Base()), SnapshotTestFixtures.Doc());
        var removed = Assert.Single(diff.Removed);
        Assert.Equal("App", removed.DisplayNameBefore);
        Assert.Null(removed.DisplayNameAfter);
        Assert.Equal(SigningStatus.Unverified, removed.SigningBefore);
    }

    [Fact]
    public void RenamedDisplay_SameTarget_IsChangedNotAddedRemoved()
    {
        var before = SnapshotTestFixtures.Doc(Base());
        var after = SnapshotTestFixtures.Doc(Base() with { DisplayName = "App Renamed!" });

        var diff = DiffEngine.Compare(before, after);

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        var changed = Assert.Single(diff.Changed);
        Assert.Contains(DiffEngine.ReasonDisplay, changed.Reasons);
        Assert.DoesNotContain(DiffEngine.ReasonPath, changed.Reasons);
        Assert.Equal("App", changed.DisplayNameBefore);
        Assert.Equal("App Renamed!", changed.DisplayNameAfter);
        Assert.Equal(changed.TargetPathsBefore, changed.TargetPathsAfter);
    }

    [Fact]
    public void MovedTargetPath_IsChangedWithBothSidesNotAddPlusRemove()
    {
        var before = SnapshotTestFixtures.Doc(Base());
        var after = SnapshotTestFixtures.Doc(Base() with
        {
            TargetPaths = new[] { @"C:\Apps\v2\app.exe" },
            StableKey = StableKey.BuildWithTargets("run-key", "user",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run\App", new[] { @"C:\Apps\v2\app.exe" }),
        });

        var diff = DiffEngine.Compare(before, after);

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        var changed = Assert.Single(diff.Changed);
        Assert.Contains(DiffEngine.ReasonPath, changed.Reasons);
        Assert.Equal(new[] { @"c:\apps\app.exe" }, changed.TargetPathsBefore);
        Assert.Equal(new[] { @"c:\apps\v2\app.exe" }, changed.TargetPathsAfter);
    }

    [Fact]
    public void SigningFlip_UnverifiedToSigned_IsChanged_KeepingStatesDistinct()
    {
        // Unverified -> Signed must read as a change; Unverified must never
        // be treated as equivalent to Unsigned.
        var before = SnapshotTestFixtures.Doc(Base());
        var after = SnapshotTestFixtures.Doc(Base() with { Signing = SigningStatus.Signed, SignerSubject = "CN=Vendor" });

        var diff = DiffEngine.Compare(before, after);
        var changed = Assert.Single(diff.Changed);
        Assert.Contains(DiffEngine.ReasonSigning, changed.Reasons);
        Assert.Equal(SigningStatus.Unverified, changed.SigningBefore);
        Assert.Equal(SigningStatus.Signed, changed.SigningAfter);
    }

    [Fact]
    public void UnverifiedToUnsigned_IsChanged_NotSilentlyEqual()
    {
        var before = SnapshotTestFixtures.Doc(Base());
        var after = SnapshotTestFixtures.Doc(Base() with { Signing = SigningStatus.Unsigned });

        var diff = DiffEngine.Compare(before, after);
        var changed = Assert.Single(diff.Changed);
        Assert.Contains(DiffEngine.ReasonSigning, changed.Reasons);
    }

    [Fact]
    public void EnabledFlip_IsChanged()
    {
        var before = SnapshotTestFixtures.Doc(Base() with { Enabled = true });
        var after = SnapshotTestFixtures.Doc(Base() with { Enabled = false });

        var diff = DiffEngine.Compare(before, after);
        var changed = Assert.Single(diff.Changed);
        Assert.Contains(DiffEngine.ReasonEnabled, changed.Reasons);
    }

    [Fact]
    public void SameNameDifferentScopeOrSourceKind_AreDifferentIdentities()
    {
        var user = SnapshotTestFixtures.Entry("run-key", "user", "Shared", "Same", new[] { "a" });
        var machine = SnapshotTestFixtures.Entry("run-key", "machine", "Shared", "Same", new[] { "a" });
        var task = SnapshotTestFixtures.Entry("scheduled-task", "user", "Shared", "Same", new[] { "a" });

        var diff = DiffEngine.Compare(
            SnapshotTestFixtures.Doc(user),
            SnapshotTestFixtures.Doc(machine, task));

        Assert.Equal(2, diff.Added.Count);
        Assert.Single(diff.Removed);
    }

    [Fact]
    public void DisplayOnlyCosmetics_WhitespaceAndCase_AreNormalizedAway()
    {
        // Normalization (trim/collapse/lower-case) applies to identity inputs,
        // so a value name that only differs cosmetically is the same entry.
        // Display names themselves are NOT identity inputs.
        var before = SnapshotTestFixtures.Doc(Base());
        var after = SnapshotTestFixtures.Doc(Base() with
        {
            // same normalized identity, different raw casing in native key
            NativeKey = StableKey.Normalize(@"hkcu\software\microsoft\windows\currentversion\run\app"),
        });

        var diff = DiffEngine.Compare(before, after);
        Assert.Single(diff.Unchanged);
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void GoldenFile_DiffClassification_IsStable()
    {
        // Golden-file test: the full serialized classification is the golden.
        // Any bucket-order, reason-wording, or evidence-field change breaks
        // this on purpose.
        var before = SnapshotTestFixtures.Doc(
            SnapshotTestFixtures.Entry("run-key", "user", @"HKCU\Run\Keep", "Keep", new[] { @"C:\a\keep.exe" }),
            SnapshotTestFixtures.Entry("run-key", "user", @"HKCU\Run\Gone", "Gone", new[] { @"C:\a\gone.exe" }),
            SnapshotTestFixtures.Entry("run-key", "user", @"HKCU\Run\Move", "Move", new[] { @"C:\a\move.exe" }),
            SnapshotTestFixtures.Entry("run-key", "user", @"HKCU\Run\Sign", "Sign", new[] { @"C:\a\sign.exe" }),
            SnapshotTestFixtures.Entry("run-key", "user", @"HKCU\Run\Rename", "Old Name", new[] { @"C:\a\ren.exe" }));
        var after = SnapshotTestFixtures.Doc(
            SnapshotTestFixtures.Entry("run-key", "user", @"HKCU\Run\Keep", "Keep", new[] { @"C:\a\keep.exe" }),
            SnapshotTestFixtures.Entry("run-key", "user", @"HKCU\Run\Move", "Move", new[] { @"C:\a\new\move.exe" }),
            SnapshotTestFixtures.Entry("run-key", "user", @"HKCU\Run\Sign", "Sign", new[] { @"C:\a\sign.exe" },
                signing: SigningStatus.Signed),
            SnapshotTestFixtures.Entry("run-key", "user", @"HKCU\Run\Rename", "New Name", new[] { @"C:\a\ren.exe" }),
            SnapshotTestFixtures.Entry("run-key", "user", @"HKCU\Run\Fresh", "Fresh", new[] { @"C:\a\fresh.exe" }));

        var json = DiffEngine.Compare(before, after).ToJson();

        const string Golden = """
{"added":[{"identityKey":"run-key|user|hkcu\\run\\fresh|c:\\a\\fresh.exe","sourceKind":"run-key","scope":"user","stableKeyAfter":"run-key|user|hkcu\\run\\fresh|c:\\a\\fresh.exe","displayNameAfter":"Fresh","targetPathsAfter":["c:\\a\\fresh.exe"],"signingAfter":"unverified","reasons":[]}],"removed":[{"identityKey":"run-key|user|hkcu\\run\\gone|c:\\a\\gone.exe","sourceKind":"run-key","scope":"user","stableKeyBefore":"run-key|user|hkcu\\run\\gone|c:\\a\\gone.exe","displayNameBefore":"Gone","targetPathsBefore":["c:\\a\\gone.exe"],"signingBefore":"unverified","reasons":[]}],"changed":[{"identityKey":"native|run-key|user|hkcu\\run\\move|0","sourceKind":"run-key","scope":"user","stableKeyBefore":"run-key|user|hkcu\\run\\move|c:\\a\\move.exe","stableKeyAfter":"run-key|user|hkcu\\run\\move|c:\\a\\new\\move.exe","displayNameBefore":"Move","displayNameAfter":"Move","targetPathsBefore":["c:\\a\\move.exe"],"targetPathsAfter":["c:\\a\\new\\move.exe"],"signingBefore":"unverified","signingAfter":"unverified","reasons":["target-path"]},{"identityKey":"run-key|user|hkcu\\run\\rename|c:\\a\\ren.exe","sourceKind":"run-key","scope":"user","stableKeyBefore":"run-key|user|hkcu\\run\\rename|c:\\a\\ren.exe","stableKeyAfter":"run-key|user|hkcu\\run\\rename|c:\\a\\ren.exe","displayNameBefore":"Old Name","displayNameAfter":"New Name","targetPathsBefore":["c:\\a\\ren.exe"],"targetPathsAfter":["c:\\a\\ren.exe"],"signingBefore":"unverified","signingAfter":"unverified","reasons":["display-renamed"]},{"identityKey":"run-key|user|hkcu\\run\\sign|c:\\a\\sign.exe","sourceKind":"run-key","scope":"user","stableKeyBefore":"run-key|user|hkcu\\run\\sign|c:\\a\\sign.exe","stableKeyAfter":"run-key|user|hkcu\\run\\sign|c:\\a\\sign.exe","displayNameBefore":"Sign","displayNameAfter":"Sign","targetPathsBefore":["c:\\a\\sign.exe"],"targetPathsAfter":["c:\\a\\sign.exe"],"signingBefore":"unverified","signingAfter":"signed","reasons":["signing-status"]}],"unchanged":[{"identityKey":"run-key|user|hkcu\\run\\keep|c:\\a\\keep.exe","sourceKind":"run-key","scope":"user","stableKeyBefore":"run-key|user|hkcu\\run\\keep|c:\\a\\keep.exe","stableKeyAfter":"run-key|user|hkcu\\run\\keep|c:\\a\\keep.exe","displayNameBefore":"Keep","displayNameAfter":"Keep","targetPathsBefore":["c:\\a\\keep.exe"],"targetPathsAfter":["c:\\a\\keep.exe"],"signingBefore":"unverified","signingAfter":"unverified","reasons":[]}]}
""";
        Assert.Equal(Golden, json);
    }
}
