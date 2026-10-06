namespace Nowify.Models;

public sealed record NowPlayingTrack(
    string Id, string Title, string[] Artists, string Album, string? ImageUrl);

public sealed record PlaybackResult(
    NowPlayingTrack? Track = null,
    string? Error = null,
    bool RequiresLogin = false,
    TimeSpan? RetryAfter = null);
