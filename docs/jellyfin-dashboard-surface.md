# Jellyfin Dashboard Surface — Metric Mirroring Reference

This document inventories everything the Jellyfin server exposes to its web UI
(HTTP endpoints + WebSocket messages). The goal is to mirror this surface 1:1
as OpenTelemetry metrics so the plugin's output can be validated against what
operators already see in the dashboard.

All paths are relative to the Jellyfin server source (`../jellyfin/`).

---

## 1. Active Sessions (the headline data)

**Endpoint:** `GET /Sessions` → `Jellyfin.Api/Controllers/SessionController.cs:52`
**Push channel:** WebSocket `SessionMessageType.Sessions`
**Primary DTO:** `MediaBrowser.Model/Dto/SessionInfoDto.cs`

### `SessionInfoDto`
- `Id`, `UserId`, `UserName`, `AdditionalUsers[]`
- `Client` (app name), `ApplicationVersion`, `DeviceId`, `DeviceName`, `DeviceType`, `HasCustomDeviceName`
- `RemoteEndPoint`, `ServerId`
- `LastActivityDate`, `LastPlaybackCheckIn`, `LastPausedDate`
- `IsActive`, `SupportsMediaControl`, `SupportsRemoteControl`
- `PlayState` (`PlayerStateInfo`)
- `NowPlayingItem` (`BaseItemDto`), `NowViewingItem`, `NowPlayingQueue[]`, `PlaylistItemId`
- `TranscodingInfo` (`TranscodingInfo`)
- `Capabilities` (`ClientCapabilitiesDto`), `SupportedCommands[]`, `PlayableMediaTypes[]`
- `UserPrimaryImageTag`

### `PlayerStateInfo` — `MediaBrowser.Model/Session/PlayerStateInfo.cs`
- `PositionTicks`, `CanSeek`, `IsPaused`, `IsMuted`, `VolumeLevel`
- `AudioStreamIndex`, `SubtitleStreamIndex`, `MediaSourceId`, `LiveStreamId`
- `PlayMethod` — `Transcode | DirectStream | DirectPlay`
- `RepeatMode` — `RepeatNone | RepeatAll | RepeatOne`
- `PlaybackOrder` — `Default | Shuffle`

### `TranscodingInfo` — `MediaBrowser.Model/Session/TranscodingInfo.cs`
- `AudioCodec`, `VideoCodec`, `Container`
- `IsVideoDirect`, `IsAudioDirect`
- `Bitrate`, `Framerate`, `CompletionPercentage`
- `Width`, `Height`, `AudioChannels`
- `HardwareAccelerationType` — `none | amf | qsv | nvenc | v4l2m2m | vaapi | videotoolbox | rkmpp`
- `TranscodeReasons` (flags) — codec/container/bitrate/resolution/profile/level/audio/subtitle reasons

### `ClientCapabilitiesDto` — `MediaBrowser.Model/Dto/ClientCapabilitiesDto.cs`
- `PlayableMediaTypes[]`, `SupportedCommands[]`, `SupportsMediaControl`,
  `SupportsPersistentIdentifier`, `DeviceProfile`, `AppStoreUrl`, `IconUrl`

### `BaseItemDto` (Now Playing item) — selected operational fields
`MediaBrowser.Model/Dto/BaseItemDto.cs` (full DTO is ~600 lines)
- `Id`, `Name`, `Type` (`BaseItemKind`), `MediaType`, `Container`
- `RunTimeTicks`, `PremiereDate`, `ProductionYear`
- `SeriesId`, `SeriesName`, `SeasonId`, `SeasonName`, `IndexNumber`, `ParentIndexNumber`
- `MediaSources[]`, `MediaStreams[]`, `VideoType`, `IsHD`, `Video3DFormat`
- `Path`, `ChannelId`, `ChannelName`
- `UserData` (`UserItemDataDto`)

---

## 2. System / Server Info

**Endpoints:**
- `GET /System/Info` → `SystemInfo`
- `GET /System/Info/Public` → `PublicSystemInfo`
- `GET /System/Info/Storage` → `SystemStorageDto`
- `GET /System/Endpoint` → `EndPointInfo`
- `GET /System/Ping`

### `PublicSystemInfo` — `MediaBrowser.Model/System/PublicSystemInfo.cs`
- `Id`, `ServerName`, `Version`, `ProductName`, `LocalAddress`, `StartupWizardCompleted`

### `SystemInfo` adds
- `HasPendingRestart`, `IsShuttingDown`, `SupportsLibraryMonitor`
- `WebSocketPortNumber`, `PackageName`
- `CompletedInstallations[]`, `CastReceiverApplications[]`

### `SystemStorageDto` — `Jellyfin.Api/Models/SystemInfoDtos/SystemStorageDto.cs`
Per-folder `FolderStorageDto` (each with `Path`, `FreeSpace`, `UsedSpace`,
`StorageType`, `DeviceId`) for:
- `ProgramDataFolder`, `WebFolder`, `ImageCacheFolder`, `CacheFolder`,
  `LogFolder`, `InternalMetadataFolder`, `TranscodingTempFolder`
- `Libraries[]` (each is `LibraryStorageDto` with `Id`, `Name`, `Folders[]`)

### `EndPointInfo`
- `IsLocal`, `IsInNetwork`

---

## 3. Activity Log

**Endpoint:** `GET /System/ActivityLog/Entries`
**DTO:** `MediaBrowser.Model/Activity/ActivityLogEntry.cs`

- `Id`, `Name`, `Overview`, `ShortOverview`
- `Type` (free-form string — e.g. `AuthenticationSucceeded`, `VideoPlayback`)
- `ItemId`, `UserId`, `Date`
- `Severity` (`LogLevel`) — `Trace | Debug | Information | Warning | Error | Critical | None`

---

## 4. Scheduled Tasks

**Endpoint:** `GET /ScheduledTasks`
**DTO:** `MediaBrowser.Model/Tasks/TaskInfo.cs`

### `TaskInfo`
- `Id`, `Key`, `Name`, `Description`, `Category`, `IsHidden`
- `State` — `Idle | Cancelling | Running`
- `CurrentProgressPercentage`
- `Triggers[]` (`TaskTriggerInfo`)
- `LastExecutionResult` (`TaskResult`)

### `TaskResult` — `MediaBrowser.Model/Tasks/TaskResult.cs`
- `StartTimeUtc`, `EndTimeUtc` (→ duration)
- `Status` — `Completed | Failed | Cancelled | Aborted`
- `Name`, `Key`, `Id`, `ErrorMessage`, `LongErrorMessage`

### `TaskTriggerInfo`
- `Type` — `DailyTrigger | WeeklyTrigger | IntervalTrigger | StartupTrigger`
- `TimeOfDayTicks`, `IntervalTicks`, `DayOfWeek`, `MaxRuntimeTicks`

---

## 5. Plugins

**Endpoint:** `GET /Plugins`
**DTO:** `MediaBrowser.Model/Plugins/PluginInfo.cs`

- `Id`, `Name`, `Version`, `Description`
- `Status` — `Active | Restart | Disabled | NotSupported | Malfunctioned | Superseded | Deleted`
- `CanUninstall`, `HasImage`, `ConfigurationFileName`

---

## 6. Users

**Endpoint:** `GET /Users`, `GET /Users/{userId}`
**DTO:** `MediaBrowser.Model/Dto/UserDto.cs`

- `Id`, `Name`, `ServerId`, `ServerName`, `PrimaryImageTag`
- `LastLoginDate`, `LastActivityDate`
- `EnableAutoLogin`
- `Configuration` (`UserConfiguration`)
- `Policy` (`UserPolicy`) — `IsAdministrator`, `IsDisabled`, `EnableRemoteAccess`,
  `EnableMediaPlayback`, `EnableLiveTvAccess`, `EnableLiveTvManagement`,
  `EnableAudio/VideoPlaybackTranscoding`, `MaxParentalRating`, `AccessSchedules[]`

---

## 7. Library Item Counts

**Endpoint:** `GET /Items/Counts`
**DTO:** `MediaBrowser.Model/Dto/ItemCounts.cs`

- `MovieCount`, `SeriesCount`, `EpisodeCount`
- `ArtistCount`, `AlbumCount`, `SongCount`, `MusicVideoCount`
- `BookCount`, `BoxSetCount`, `TrailerCount`, `ProgramCount`
- `ItemCount` (total)

---

## 8. Live TV

Routes under `Jellyfin.Api/Controllers/LiveTvController.cs`. Surface categories:

- `GET /LiveTv/Info` — manager status
- `GET /LiveTv/Channels` — channel list
- `GET /LiveTv/Recordings`, `/Recordings/Series`, `/Recordings/Groups`,
  `/Recordings/Folders` — recordings (incl. in-progress)
- `GET /LiveTv/Timers`, `/Timers/Defaults` — one-shot timers
- `GET /LiveTv/SeriesTimers` — recurring timers
- `GET /LiveTv/Programs`, `/Programs/Recommended` — guide
- `POST /LiveTv/Tuners/{tunerId}/Reset` — tuner control
- `GET /LiveTv/Tuners/Discover` — tuner discovery
- `GET /LiveTv/GuideInfo` — guide configuration

---

## 9. Logs

**Endpoints:** `GET /System/Logs`, `GET /System/Logs/Log?name=...`
**DTO:** `LogFile` — `Name`, `Size`, `DateCreated`, `DateModified`

---

## 10. WebSocket Push Channels

`SessionMessageType` enum — `MediaBrowser.Model/Session/SessionMessageType.cs`.
These are the *real-time* events the web UI subscribes to. Each represents a
candidate signal — count occurrences, durations, or state changes.

### Server → Client
| Message | Maps to |
|---|---|
| `Sessions` | Active session snapshot (periodic + on-event) |
| `Playstate` | Playback control state change |
| `UserDataChanged` | Watched/favorite/progress changed |
| `LibraryChanged` | Item added/updated/removed |
| `RefreshProgress` | Metadata refresh progress |
| `ScheduledTaskEnded` | Task completion |
| `ScheduledTasksInfo` | Periodic task state snapshot |
| `ActivityLogEntry` | New activity log entry |
| `TimerCreated` / `TimerCancelled` | Live TV one-shot timer |
| `SeriesTimerCreated` / `SeriesTimerCancelled` | Live TV series timer |
| `PackageInstalling` | Plugin install progress |
| `PackageInstallationCompleted` / `Failed` / `Cancelled` | Plugin install terminal states |
| `PackageUninstalled` | Plugin removed |
| `UserUpdated` / `UserDeleted` | User lifecycle |
| `Play` | Playback command sent |
| `GeneralCommand` | General command sent |
| `SyncPlayCommand` / `SyncPlayGroupUpdate` | SyncPlay group state |
| `RestartRequired` | Pending restart raised |
| `ServerShuttingDown` / `ServerRestarting` | Server lifecycle |
| `ForceKeepAlive` | Heartbeat requirement notice |

### Client → Server (subscription control)
- `SessionsStart` / `SessionsStop`
- `ScheduledTasksInfoStart` / `ScheduledTasksInfoStop`
- `ActivityLogEntryStart` / `ActivityLogEntryStop`

### Bidirectional
- `KeepAlive`

---

## Suggested initial metric mapping

Mirror each dashboard panel as one or more OTel instruments. Most map cleanly
onto the `ISessionManager` / `ITaskManager` / `ILibraryManager` events already
catalogued in [[jellyfin-events]] — so the plugin can react in real time
without polling the HTTP endpoints.

| Dashboard panel | Source | OTel instrument | Notes |
|---|---|---|---|
| Active sessions count | `ISessionManager.Sessions` | `ObservableGauge<long>` | Snapshot on each scrape |
| Now-playing breakdown | `PlaybackStart`/`Stopped` events | `Counter` + `Histogram` | Attributes: `play_method`, `media_type`, `client`, `is_transcoding`, `hw_accel` |
| Transcode reasons | `TranscodingInfo.TranscodeReasons` on playback start | `Counter` | One attribute per flag; helps identify client-profile gaps |
| Storage free/used | `GET /System/Info/Storage` | `ObservableGauge<long>` | Per folder + per library; sample on a long interval (60s+) |
| Library item counts | `GET /Items/Counts` | `ObservableGauge<long>` | One series per type; update on `ItemAdded`/`Removed` or periodic poll |
| Scheduled task duration | `ITaskManager.TaskCompleted` | `Histogram` | Attributes: `task_key`, `status` |
| Scheduled task state | `TaskInfo.State` | `ObservableGauge` | Per-task gauge of running/idle |
| Plugin status | `GET /Plugins` | `ObservableGauge<long>` | Count grouped by `Status` |
| Pending restart | `IApplicationHost.HasPendingRestartChanged` | `ObservableGauge<long>` | 0/1 |
| Activity log severity | `ActivityLogEntry` push | `Counter` | Attributes: `type`, `severity` |
| User activity | `UserDto.LastActivityDate` | `ObservableGauge` | Or counter from `SessionStarted` |
| Live TV recordings | `ILiveTvManager` events | `Counter` + `ObservableGauge` | Active timers/recordings |

Mapping is intentionally one-to-one with what the UI displays — that way you
can sit the plugin's Grafana board next to the Jellyfin dashboard and confirm
every number matches before adding anything novel.

### Event-driven vs snapshot sources

Most of these signals can be driven from `ISessionManager` / `ILibraryManager` /
`ITaskManager` / `IUserDataManager` events — no polling required, and the
plugin reacts in real time. But a few panels are inherently snapshot data and
have no corresponding event:

- **Storage free/used** (`GET /System/Info/Storage`) — filesystem state
- **Item counts** (`GET /Items/Counts`) — could be event-derived from
  `ItemAdded`/`ItemRemoved`, but a periodic resync is safer
- **Plugin list / status** (`GET /Plugins`) — via `IInstallationManager` /
  plugin manager
- **User list** (`GET /Users`) — via `IUserManager`
- **Scheduled task list** (`GET /ScheduledTasks`) — via `ITaskManager.ScheduledTasks`

For these, use an `ObservableGauge` whose callback queries the relevant
**injected manager** at scrape time. Do **not** have the plugin make HTTP
calls back into its own server — resolve the manager from DI and read it
in-process.

See also: [[jellyfin-events]] for the underlying event surface.
