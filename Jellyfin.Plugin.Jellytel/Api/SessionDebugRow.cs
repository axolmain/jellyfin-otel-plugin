namespace Jellyfin.Plugin.Jellytel.Api;

/// <summary>
/// One projected session row in <see cref="SessionDebugDto"/>.
/// </summary>
public sealed class SessionDebugRow
{
    /// <summary>Gets or sets the session identifier.</summary>
    public string? SessionId { get; set; }

    /// <summary>Gets or sets the user the session belongs to.</summary>
    public string? UserName { get; set; }

    /// <summary>Gets or sets the client app name.</summary>
    public string? Client { get; set; }

    /// <summary>Gets or sets the device name.</summary>
    public string? DeviceName { get; set; }

    /// <summary>Gets or sets the remote endpoint (PII — surfaced for debugging only).</summary>
    public string? RemoteEndPoint { get; set; }

    /// <summary>Gets or sets the play method (<c>DirectPlay</c> / <c>DirectStream</c> / <c>Transcode</c> / <c>null</c>).</summary>
    public string? PlayMethod { get; set; }

    /// <summary>Gets or sets the name of the now-playing item, or null when nothing is playing.</summary>
    public string? NowPlayingItem { get; set; }

    /// <summary>Gets or sets the active media source id reported by the client.</summary>
    public string? MediaSourceId { get; set; }

    /// <summary>Gets or sets the count of media sources attached to the now-playing item.</summary>
    public int MediaSourcesCount { get; set; }

    /// <summary>Gets or sets the bitrate of the matched media source (bits/s), or null if unresolved.</summary>
    public long? MediaSourceBitrate { get; set; }

    /// <summary>Gets or sets the source video frame rate (<c>ReferenceFrameRate</c>), or null if unresolved.</summary>
    public float? SourceFps { get; set; }

    /// <summary>Gets or sets the source video codec, or null if unresolved.</summary>
    public string? SourceVideoCodec { get; set; }

    /// <summary>Gets or sets the transcoded output bitrate (bits/s) if transcoding.</summary>
    public long? TranscodingBitrate { get; set; }

    /// <summary>Gets or sets the encoder output frame rate if transcoding.</summary>
    public float? TranscodingFramerate { get; set; }

    /// <summary>Gets or sets the transcoder hardware-acceleration type if transcoding.</summary>
    public string? TranscodingHwAccel { get; set; }

    /// <summary>Gets or sets the transcoder target video codec.</summary>
    public string? TranscodingVideoCodec { get; set; }

    /// <summary>Gets or sets the comma-separated transcode-reason flags.</summary>
    public string? TranscodeReasons { get; set; }

    /// <summary>Gets or sets a value indicating whether the host considers this session active.</summary>
    public bool IsActive { get; set; }

    /// <summary>Gets or sets the session's <c>LastActivityDate</c> (unix ms).</summary>
    public long LastActivityMs { get; set; }

    /// <summary>Gets or sets the session's <c>LastPlaybackCheckIn</c> (unix ms).</summary>
    public long LastPlaybackCheckInMs { get; set; }

    /// <summary>Gets or sets seconds since the last playback check-in.</summary>
    public double SecondsSinceLastCheckIn { get; set; }

    /// <summary>Gets or sets a value indicating whether the plugin's staleness filter considers this session live.</summary>
    public bool PassesStaleFilter { get; set; }

    /// <summary>Gets or sets the resolved outbound bitrate the snapshotter would attribute to this session (bits/s).</summary>
    public long ResolvedOutboundBitrate { get; set; }
}
