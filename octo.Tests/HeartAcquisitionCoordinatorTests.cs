using Microsoft.Extensions.Logging;
using Moq;
using Octo.Models.Settings;
using Octo.Services;
using Octo.Services.Common;
using Octo.Services.Lidarr;

namespace Octo.Tests;

public class HeartAcquisitionCoordinatorTests
{
    [Fact]
    public void LidarrSourceRoutesTrackAndAlbumWithoutCallingDirectDownloader()
    {
        var lidarr = new Mock<ILidarrHeartAcquisitionService>();
        var direct = new Mock<IDownloadService>();
        var queue = new TrackAcquisitionQueue(new Mock<ILogger<TrackAcquisitionQueue>>().Object);
        var coordinator = new HeartAcquisitionCoordinator(
            TestOptions.Monitor(new SubsonicSettings { DownloadSource = DownloadSource.Lidarr }),
            queue, direct.Object, lidarr.Object);

        coordinator.QueueTrack("soulseek", "track-id");
        coordinator.QueueAlbum("soulseek", "album-id");

        lidarr.Verify(x => x.QueueTrack("soulseek", "track-id"), Times.Once);
        lidarr.Verify(x => x.QueueAlbum("soulseek", "album-id"), Times.Once);
        direct.Verify(x => x.DownloadRemainingAlbumTracksInBackground(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void SoulseekSourceKeepsAlbumOnExistingDirectPath()
    {
        var lidarr = new Mock<ILidarrHeartAcquisitionService>();
        var direct = new Mock<IDownloadService>();
        var queue = new TrackAcquisitionQueue(new Mock<ILogger<TrackAcquisitionQueue>>().Object);
        var coordinator = new HeartAcquisitionCoordinator(
            TestOptions.Monitor(new SubsonicSettings { DownloadSource = DownloadSource.Soulseek }),
            queue, direct.Object, lidarr.Object);

        coordinator.QueueAlbum("soulseek", "album-id");

        direct.Verify(x => x.DownloadRemainingAlbumTracksInBackground("soulseek", "album-id", ""), Times.Once);
        lidarr.VerifyNoOtherCalls();
    }

    [Fact]
    public void DownloadSourceChangeTakesEffectWithoutRebuildingCoordinator()
    {
        var lidarr = new Mock<ILidarrHeartAcquisitionService>();
        var direct = new Mock<IDownloadService>();
        var queue = new TrackAcquisitionQueue(new Mock<ILogger<TrackAcquisitionQueue>>().Object);
        var settings = TestOptions.Monitor(
            new SubsonicSettings { DownloadSource = DownloadSource.Soulseek });
        var coordinator = new HeartAcquisitionCoordinator(settings, queue, direct.Object, lidarr.Object);

        coordinator.QueueAlbum("soulseek", "first-album");
        settings.Set(new SubsonicSettings { DownloadSource = DownloadSource.Lidarr });
        coordinator.QueueAlbum("soulseek", "second-album");

        direct.Verify(x => x.DownloadRemainingAlbumTracksInBackground(
            "soulseek", "first-album", ""), Times.Once);
        lidarr.Verify(x => x.QueueAlbum("soulseek", "second-album"), Times.Once);
    }
}
