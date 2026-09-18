using System.IO;

namespace AutostartAudit.Core.Scan;

/// <summary>
/// Filesystem mutation boundary used only by the quarantine layer. Failures
/// map to the same explicit vocabulary as <see cref="IFolderProbe"/>.
/// </summary>
public interface IFileSystemMutator
{
    /// <summary>Creates <paramref name="path"/> and all parent directories if missing. Throws on failure.</summary>
    void EnsureDirectory(string path);

    /// <summary>Current file length, or null when the file provably does not exist. Throws on failure.</summary>
    public long? TryGetLength(string path);

    public FileAttributes? TryGetAttributes(string path);

    public void SetAttributes(string path, FileAttributes attributes);

    /// <summary>Renames/moves an existing file; used for collision-free placement inside the quarantine folder.</summary>
    public bool TryExclusiveMove(string source, string target);
}

/// <summary>OS-boundary implementation of <see cref="IFileSystemMutator"/>.</summary>
public sealed class SystemFileMutator : IFileSystemMutator
{
    public void EnsureDirectory(string path) => Directory.CreateDirectory(path);

    public long? TryGetLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new FolderAccessDeniedException(path, ex.Message);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            return null;
        }
        catch (IOException ex)
        {
            throw new FolderUnknownException(path, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new FolderAccessDeniedException(path, ex.Message);
        }
        catch (IOException ex)
        {
            throw new FolderUnknownException(path, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public void SetAttributes(string path, FileAttributes attributes)
    {
        try
        {
            File.SetAttributes(path, attributes);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new FolderAccessDeniedException(path, ex.Message);
        }
        catch (IOException ex)
        {
            throw new FolderUnknownException(path, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public bool TryExclusiveMove(string source, string target)
    {
        try
        {
            File.Move(source, target, overwrite: false);
            return true;
        }
        catch (IOException)
        {
            return false; // target exists — caller picks another name
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new FolderAccessDeniedException(source, ex.Message);
        }
    }
}
