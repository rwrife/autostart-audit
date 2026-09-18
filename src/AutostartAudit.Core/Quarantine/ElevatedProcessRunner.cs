using System.ComponentModel;
using System.Diagnostics;

namespace AutostartAudit.Core.Quarantine;

/// <summary>
/// Single-action elevation by re-launching this executable elevated for one
/// already-journaled record. The record lives in the shared journal database
/// (its path is passed explicitly, because the elevated child resolves
/// %LOCALAPPDATA% under the consented administrator profile, not this user's).
/// The child performs the whole mutation — so a declined prompt, a crashed
/// child, or a killed UAC dialog can never leave a partial write: the record
/// stays Pending and the tool reports the state honestly.
/// </summary>
public sealed class ElevatedProcessRunner : IElevatedRecordRunner
{
    private const int ERROR_CANCELLED = 1223;

    private readonly Func<string> _childExecutable;
    private readonly string _journalPath;

    /// <param name="childExecutable">Resolves the executable to relaunch (normally the running process path).</param>
    /// <param name="journalPath">Absolute path of the journal DB the child must open.</param>
    public ElevatedProcessRunner(Func<string> childExecutable, string journalPath)
    {
        _childExecutable = childExecutable;
        _journalPath = journalPath;
    }

    public ElevatedRunStatus RunQuarantine(long recordId) =>
        Run($"run-record {recordId} quarantine");

    public ElevatedRunStatus RunRestore(long recordId) =>
        Run($"run-record {recordId} restore");

    private ElevatedRunStatus Run(string arguments)
    {
        if (!OperatingSystem.IsWindows())
            return ElevatedRunStatus.NotAvailable;

        string executable;
        try
        {
            executable = _childExecutable();
        }
        catch (Exception)
        {
            return ElevatedRunStatus.NotAvailable;
        }
        if (string.IsNullOrEmpty(executable))
            return ElevatedRunStatus.NotAvailable;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"{arguments} --journal \"{_journalPath}\"",
                UseShellExecute = true, // required for the runas verb
                Verb = "runas",
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("could not start the elevated helper process");
            process.WaitForExit();
            return process.ExitCode == 0 ? ElevatedRunStatus.Completed : ElevatedRunStatus.Uncertain;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            // The user pressed No at the UAC prompt: zero writes happened.
            return ElevatedRunStatus.Declined;
        }
        catch (Exception)
        {
            return ElevatedRunStatus.NotAvailable;
        }
    }
}
