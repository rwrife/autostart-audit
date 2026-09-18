using System.IO;
using System.Security.Cryptography;
using System.Text;
using AutostartAudit.Core.Model;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Quarantine;

/// <summary>
/// Strategy for Startup-folder files: the file is moved into an app-managed
/// quarantine folder with the original path + attributes journaled, so
/// restore moves the exact file back to its exact place. The quarantine root
/// lives under %LOCALAPPDATA% (never inside a Startup folder itself, which
/// would re-trigger the entry).
/// </summary>
public sealed class StartupFolderQuarantineStrategy : IQuarantineStrategy
{
    public const string Kind = AutostartAudit.Core.Scan.StartupFolderSource.Kind;

    private readonly IFileSystemMutator _fs;
    private readonly string _quarantineRoot;

    public StartupFolderQuarantineStrategy(IFileSystemMutator fs, string quarantineRoot)
    {
        _fs = fs;
        _quarantineRoot = Path.GetFullPath(quarantineRoot);
    }

    internal sealed record State(
        string OriginalPath,
        string QuarantinePath,
        long Length,
        FileAttributes Attributes);

    public string SourceKind => Kind;

    public QuarantineDecision Plan(AutoStartEntry entry)
    {
        var original = (entry.NativeKey ?? entry.StableKey).Replace('/', Path.DirectorySeparatorChar);
        var fileName = Path.GetFileName(original);
        if (fileName.Length == 0 || Path.GetDirectoryName(original) is null)
            return QuarantineDecision.ReadOnly("startup-folder identity is not a full file path");

        try
        {
            var attributes = _fs.TryGetAttributes(original);
            if (attributes is null)
                return QuarantineDecision.ReadOnly("file is no longer present at its journaled path");
            var length = _fs.TryGetLength(original);
            if (length is null)
                return QuarantineDecision.ReadOnly("file length is unreadable; exact restore evidence is incomplete");

            var scanSize = ParseScanSize(entry.RawValueSnapshot);
            if (scanSize is not null && scanSize.Value != length.Value)
                return QuarantineDecision.ReadOnly(
                    $"file size changed since the scan (scan={scanSize.Value}, now={length.Value}); refusing to quarantine drifted state");

            return QuarantineDecision.Ready(BeforeState.Serialize(new State(
                original, PickQuarantinePath(original, fileName), length.Value, attributes.Value)));
        }
        catch (Exception ex)
        {
            return QuarantineDecision.ReadOnly($"current file state unreadable: {ex.Message}");
        }
    }

    public MutationResult Execute(string beforeStateJson) => Move(StateAction.Quarantine, beforeStateJson);

    public MutationResult Restore(string beforeStateJson) => Move(StateAction.Restore, beforeStateJson);

    private enum StateAction { Quarantine, Restore }

    private MutationResult Move(StateAction action, string beforeStateJson)
    {
        var state = BeforeState.Deserialize<State>(beforeStateJson);
        if (state is null)
            return new MutationResult(MutationStatus.Failed, "journaled before-state failed to deserialize");
        var from = action == StateAction.Quarantine ? state.OriginalPath : state.QuarantinePath;
        var to = action == StateAction.Quarantine ? state.QuarantinePath : state.OriginalPath;
        try
        {
            if (_fs.TryGetAttributes(from) is null)
                return new MutationResult(MutationStatus.Failed, $"source file is missing ({from}); nothing was moved");
            if (_fs.TryGetAttributes(to) is not null)
                return new MutationResult(MutationStatus.Failed,
                    $"target path is already occupied ({to}); refusing to clobber current state");

            _fs.EnsureDirectory(Path.GetDirectoryName(to)!);
            if (!_fs.TryExclusiveMove(from, to))
                return new MutationResult(MutationStatus.Failed, "move lost a race with another writer; nothing was moved");

            // Post-state verification: the file must exist at the destination
            // and be gone from the source.
            if (_fs.TryGetAttributes(to) is null || _fs.TryGetAttributes(from) is not null)
                return new MutationResult(MutationStatus.Failed, "post-move verification failed; inspect both paths");

            // Attributes must not silently change across the move.
            var after = _fs.TryGetAttributes(to);
            if (after is not null && after.Value != state.Attributes)
                _fs.SetAttributes(to, state.Attributes);
            return new MutationResult(MutationStatus.Ok);
        }
        catch (FolderAccessDeniedException ex)
        {
            return new MutationResult(MutationStatus.Denied, ex.Message);
        }
        catch (Exception ex)
        {
            return new MutationResult(MutationStatus.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Deterministic collision-aware quarantine path: a stable subfolder per
    /// original directory, plus a numeric suffix if the file name is taken by
    /// an earlier quarantine of a different file.
    /// </summary>
    private string PickQuarantinePath(string original, string fileName)
    {
        var parent = Path.GetDirectoryName(original)!;
        var parentHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(StableKey.Normalize(parent)))).ToLowerInvariant()[..16];
        var dir = Path.Combine(_quarantineRoot, parentHash);
        var candidate = Path.Combine(dir, fileName);
        var attempt = 1;
        while (_fs.TryGetAttributes(candidate) is not null && attempt < 1000)
        {
            candidate = Path.Combine(dir, $"{fileName}.q{attempt}");
            attempt++;
        }
        return candidate;
    }

    private static long? ParseScanSize(string snapshot)
    {
        const string marker = "(size=";
        var start = snapshot.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;
        var end = snapshot.IndexOf(')', start + marker.Length);
        if (end < 0 || !long.TryParse(snapshot[(start + marker.Length)..end], out var size))
            return null;
        return size;
    }
}
