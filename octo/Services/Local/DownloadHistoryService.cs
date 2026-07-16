using System.Text.Json;
using Octo.Models.Download;

namespace Octo.Services.Local;

/// <summary>
/// Persistent, bounded log of songs Octo has fetched. Written to a JSON file next
/// to the settings file so it survives restarts and container recreation (the
/// config dir is a host bind-mount). Newest entries first; capped so it can't grow
/// without limit. Best-effort — a write failure never breaks a download.
/// </summary>
public class DownloadHistoryService
{
    private const int MaxEntries = 500;

    private readonly string _path;
    private readonly ILogger<DownloadHistoryService> _logger;
    private readonly object _lock = new();
    private List<DownloadHistoryEntry>? _cache;

    public DownloadHistoryService(string path, ILogger<DownloadHistoryService> logger)
    {
        _path = path;
        _logger = logger;
    }

    /// <summary>Append a fetched-song entry (newest first) and persist.</summary>
    public void Record(DownloadHistoryEntry entry)
    {
        lock (_lock)
        {
            var list = LoadLocked();
            list.Insert(0, entry);
            while (list.Count > MaxEntries) list.RemoveAt(list.Count - 1);
            SaveLocked(list);
        }
    }

    /// <summary>The most recent entries, newest first.</summary>
    public IReadOnlyList<DownloadHistoryEntry> GetRecent(int limit = 200)
    {
        lock (_lock)
        {
            return LoadLocked().Take(Math.Max(0, limit)).ToList();
        }
    }

    private List<DownloadHistoryEntry> LoadLocked()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                _cache = string.IsNullOrWhiteSpace(json)
                    ? new List<DownloadHistoryEntry>()
                    : JsonSerializer.Deserialize<List<DownloadHistoryEntry>>(json) ?? new List<DownloadHistoryEntry>();
            }
            else
            {
                _cache = new List<DownloadHistoryEntry>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Download history load failed ({Msg}); starting fresh", ex.Message);
            _cache = new List<DownloadHistoryEntry>();
        }
        return _cache;
    }

    private void SaveLocked(List<DownloadHistoryEntry> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Download history save failed: {Msg}", ex.Message);
        }
    }
}
