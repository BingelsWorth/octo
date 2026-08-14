namespace Octo.Services.Admin;

/// <summary>One directory offered to the picker.</summary>
public record BrowseEntry(string Name, string Path, bool Writable);

/// <summary>A directory listing: where we are, where up is, and what is here.</summary>
public record BrowseResult(
    string Path,
    string? Parent,
    string Separator,
    bool Writable,
    bool Exists,
    IReadOnlyList<BrowseEntry> Entries,
    bool Truncated);

/// <summary>
/// Lists directories so the admin UI can pick a download folder instead of the user
/// typing one from memory and discovering later that downloads landed somewhere
/// Navidrome never scans.
///
/// This browses Octo's OWN view of the filesystem, which under Docker is the
/// container's mount namespace, not the host's drives. That is the useful view: what
/// matters is where Octo can write, not where the host thinks the files are.
///
/// Directories only, never file names. Choosing a library folder does not need them,
/// and leaving them out keeps the amount this endpoint can disclose to the minimum
/// the feature actually requires.
/// </summary>
public class DirectoryBrowser
{
    /// <summary>A pathological directory must not hang the UI or the response.</summary>
    public const int MaxEntries = 1000;

    private readonly ILogger<DirectoryBrowser> _logger;

    public DirectoryBrowser(ILogger<DirectoryBrowser> logger) => _logger = logger;

    public BrowseResult Browse(string? path)
    {
        var separator = Path.DirectorySeparatorChar.ToString();

        // No path means "start somewhere sensible", which differs by platform: on
        // Windows there is no single root to enumerate, so offer the drives.
        if (string.IsNullOrWhiteSpace(path))
        {
            return OperatingSystem.IsWindows()
                ? new BrowseResult("", null, separator, false, true, Drives(), false)
                : Listing("/", separator);
        }

        // Canonicalise before anything else so what we list is exactly what we
        // report back, and a caller cannot describe one directory and be shown
        // another via `..` segments.
        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _logger.LogDebug("browse: rejected path {Path}: {Msg}", path, ex.Message);
            return new BrowseResult(path, null, separator, false, false, Array.Empty<BrowseEntry>(), false);
        }

        return Listing(full, separator);
    }

    private static List<BrowseEntry> Drives() =>
        DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new BrowseEntry(d.Name, d.RootDirectory.FullName, IsWritable(d.RootDirectory.FullName)))
            .ToList();

    private BrowseResult Listing(string full, string separator)
    {
        if (!Directory.Exists(full))
            return new BrowseResult(full, ParentOf(full), separator, false, false, Array.Empty<BrowseEntry>(), false);

        var entries = new List<BrowseEntry>();
        var truncated = false;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(full))
            {
                if (entries.Count >= MaxEntries) { truncated = true; break; }
                try
                {
                    entries.Add(new BrowseEntry(Path.GetFileName(dir), dir, IsWritable(dir)));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // One unreadable subdirectory must not fail the whole listing.
                    _logger.LogDebug("browse: skipping {Dir}: {Msg}", dir, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug("browse: cannot enumerate {Path}: {Msg}", full, ex.Message);
            return new BrowseResult(full, ParentOf(full), separator, false, true, Array.Empty<BrowseEntry>(), false);
        }

        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return new BrowseResult(full, ParentOf(full), separator, IsWritable(full), true, entries, truncated);
    }

    private static string? ParentOf(string full)
    {
        try { return Directory.GetParent(full)?.FullName; }
        catch { return null; }
    }

    /// <summary>
    /// Prove write access by writing, rather than inferring it from existence.
    /// The whole point of the picker is to stop a user choosing a folder Octo
    /// cannot download into, and Directory.Exists says nothing about that.
    /// </summary>
    internal static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".octo-write-probe-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
