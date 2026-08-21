using Microsoft.Extensions.Options;
using Octo.Models.Settings;
using Octo.Services.Lidarr;

namespace Octo.Services.Common;

/// <summary>Routes explicit heart gestures without changing playback acquisition.</summary>
public sealed class HeartAcquisitionCoordinator
{
    private readonly SubsonicSettings _settings;
    private readonly TrackAcquisitionQueue _directQueue;
    private readonly IDownloadService _directDownloads;
    private readonly ILidarrHeartAcquisitionService _lidarr;

    public HeartAcquisitionCoordinator(IOptions<SubsonicSettings> settings,
        TrackAcquisitionQueue directQueue, IDownloadService directDownloads,
        ILidarrHeartAcquisitionService lidarr)
    {
        _settings = settings.Value;
        _directQueue = directQueue;
        _directDownloads = directDownloads;
        _lidarr = lidarr;
    }

    public void QueueTrack(string provider, string externalId)
    {
        if (_settings.DownloadSource == DownloadSource.Lidarr)
            _lidarr.QueueTrack(provider, externalId);
        else
            _ = _directQueue.Enqueue(provider, externalId, isStar: true,
                triggerAlbumDownload: true, forcePermanent: true);
    }

    public void QueueAlbum(string provider, string albumExternalId)
    {
        if (_settings.DownloadSource == DownloadSource.Lidarr)
            _lidarr.QueueAlbum(provider, albumExternalId);
        else
            _directDownloads.DownloadRemainingAlbumTracksInBackground(provider, albumExternalId, "");
    }
}
