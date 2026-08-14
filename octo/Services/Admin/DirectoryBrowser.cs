namespace Octo.Services.Admin;

/// <summary>One directory offered to the picker.</summary>
public record BrowseEntry(string Name, string Path, bool Writable);

/// <summary>A directory listing: where we are, where up is, and what is here.
/// <paramref name="AudioFiles"/> counts audio files directly in this folder. It
/// exists because listing directories alone makes a flat library — thousands of
/// loose tracks and a handful of album folders — look almost empty, giving no way
/// to tell the right folder from a stray one.</summary>
public record BrowseResult(
    string Path,
    string? Parent,
    string Separator,
    bool Writable,
    bool Exists,
    IReadOnlyList<BrowseEntry> Entries,
    bool Truncated,
    int AudioFiles);

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

    /// <summary>
    /// Extensions counted as music. Only the count is ever reported, never the
    /// file names, so the picker can say "2,352 tracks here" without turning into
    /// a file lister.
    /// </summary>
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".oga", ".opus",
        ".wav", ".wma", ".aiff", ".aif", ".ape", ".wv", ".mpc", ".dsf", ".dff",
    };

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
                ? new BrowseResult("", null, separator, false, true, Drives(), false, 0)
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
            return new BrowseResult(path, null, separator, false, false, Array.Empty<BrowseEntry>(), false, 0);
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
            return new BrowseResult(full, ParentOf(full), separator, false, false, Array.Empty<BrowseEntry>(), false, 0);

        var entries = new List<BrowseEntry>();
        var truncated = false;
        var audioFiles = 0;
        try
        {
            // One pass over the directory yields both the subfolders and the audio
            // count. Counting is free here because the enumeration is already
            // happening; doing it per subfolder instead would cost a round trip
            // each, and on a cloud mount that is seconds per folder.
            foreach (var item in Directory.EnumerateFileSystemEntries(full))
            {
                try
                {
                    if (Directory.Exists(item))
                    {
                        if (entries.Count >= MaxEntries) { truncated = true; continue; }
                        entries.Add(new BrowseEntry(Path.GetFileName(item), item, IsWritable(item)));
                    }
                    else if (AudioExtensions.Contains(Path.GetExtension(item)))
                    {
                        audioFiles++;
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // One unreadable entry must not fail the whole listing.
                    _logger.LogDebug("browse: skipping {Item}: {Msg}", item, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug("browse: cannot enumerate {Path}: {Msg}", full, ex.Message);
            return new BrowseResult(full, ParentOf(full), separator, false, true, Array.Empty<BrowseEntry>(), false, 0);
        }

        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return new BrowseResult(full, ParentOf(full), separator, IsWritable(full), true, entries, truncated, audioFiles);
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
