using System;
using System.Threading;
using System.Threading.Tasks;

namespace Secondary.Linux.Home;

public enum MediaCommand { Previous, Toggle, Next }

// One player's state as the media widget shows it. ArtworkPath is a local file only.
public sealed record MediaSnapshot(string Player, string Title, string Artist, bool Playing, bool CanPrevious, bool CanNext,
    bool CanPlay, bool CanPause, TimeSpan? Position, TimeSpan? Duration, string? ArtworkPath);

// Shared by all media widgets, like the Windows MediaWidgetSource.
public interface IMediaSource
{
    Task<MediaSnapshot?> ReadAsync(CancellationToken token);
    Task<bool> ControlAsync(MediaCommand command, CancellationToken token);
    IDisposable Subscribe(Action changed);
}
