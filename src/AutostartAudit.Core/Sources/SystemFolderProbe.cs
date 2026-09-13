namespace AutostartAudit.Core.Scan;

/// <summary>
/// OS-boundary folder probe. DirectoryNotFoundException is honest absence
/// (null); access-denied and unexplained failures map to the explicit
/// exception vocabulary.
/// </summary>
public sealed class SystemFolderProbe : IFolderProbe
{
    public IReadOnlyList<FsEntry>? TryEnumerate(string folderPath)
    {
        try
        {
            var dir = new DirectoryInfo(folderPath);
            if (!dir.Exists)
                return null; // provably absent
            var items = new List<FsEntry>();
            foreach (var entry in dir.EnumerateFileSystemInfos())
                items.Add(new FsEntry(entry.Name, entry is DirectoryInfo, entry is FileInfo f ? f.Length : 0));
            return items;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new FolderAccessDeniedException(folderPath, ex.Message);
        }
        catch (DirectoryNotFoundException)
        {
            return null; // provably absent
        }
        catch (IOException ex)
        {
            throw new FolderUnknownException(folderPath, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
