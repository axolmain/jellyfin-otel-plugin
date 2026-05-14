# Jellyfin Events Catalog

A reference of every event the Jellyfin server exposes that this plugin can
subscribe to or react to. Two subscription patterns exist:

1. **Direct event handlers** on manager interfaces (classic `event` keyword) —
   resolve the manager from DI and subscribe with `+=`.
2. **`IEventConsumer<T>` pattern** — implement the interface and register it in
   the plugin's `IPluginServiceRegistrator`; the host's `IEventManager`
   dispatches matching events to your consumer.

All file paths below are relative to the Jellyfin source tree
(`../jellyfin/` from this plugin repo).

---

## Direct event handlers

### `ISessionManager`
`MediaBrowser.Controller/Session/ISessionManager.cs`

| Event | EventArgs | Fires when |
|---|---|---|
| `PlaybackStart` | `PlaybackProgressEventArgs` | Playback begins on an item |
| `PlaybackProgress` | `PlaybackProgressEventArgs` | Periodic progress tick during playback |
| `PlaybackStopped` | `PlaybackStopEventArgs` | Playback ends (carries `PlayedToCompletion`) |
| `SessionStarted` | `SessionEventArgs` | A new session is established |
| `SessionEnded` | `SessionEventArgs` | A session is terminated |
| `SessionActivity` | `SessionEventArgs` | Any activity occurs within a session |
| `SessionControllerConnected` | `SessionEventArgs` | A session controller connects |
| `CapabilitiesChanged` | `SessionEventArgs` | Client capabilities are reported/changed |

`PlaybackProgressEventArgs` carries `Users`, `PlaybackPositionTicks`, `Item`,
`MediaInfo`, `MediaSourceId`, `IsPaused`, `IsAutomated`, `DeviceId`,
`DeviceName`, `ClientName`, `PlaySessionId`, `Session`.

### `ILibraryManager`
`MediaBrowser.Controller/Library/ILibraryManager.cs`

| Event | EventArgs | Fires when |
|---|---|---|
| `ItemAdded` | `ItemChangeEventArgs` | Item is added to the library |
| `ItemUpdated` | `ItemChangeEventArgs` | Item metadata is updated |
| `ItemRemoved` | `ItemChangeEventArgs` | Item is deleted from the library |

`ItemChangeEventArgs` carries `Item`, `Parent`, `UpdateReason`
(`ItemUpdateType`).

### `IUserDataManager`
`MediaBrowser.Controller/Library/IUserDataManager.cs`

| Event | EventArgs | Fires when |
|---|---|---|
| `UserDataSaved` | `UserDataSaveEventArgs` | Watched / favorite / progress state changes |

`UserDataSaveEventArgs` carries `UserId`, `Keys`, `SaveReason`, `UserData`,
`Item`.

### `IUserManager`
`MediaBrowser.Controller/Library/IUserManager.cs`

| Event | EventArgs | Fires when |
|---|---|---|
| `OnUserUpdated` | `GenericEventArgs<User>` | A user's profile is updated |

User creation, deletion, password changes, and lock-outs go through
`IEventConsumer<T>` instead — see below.

### `ICollectionManager`
`MediaBrowser.Controller/Collections/ICollectionManager.cs`

| Event | EventArgs | Fires when |
|---|---|---|
| `CollectionCreated` | `CollectionCreatedEventArgs` | A new collection (BoxSet) is created |
| `ItemsAddedToCollection` | `CollectionModifiedEventArgs` | Items are added to a collection |
| `ItemsRemovedFromCollection` | `CollectionModifiedEventArgs` | Items are removed from a collection |

### `IConfigurationManager` / `IServerConfigurationManager`
`MediaBrowser.Common/Configuration/IConfigurationManager.cs`,
`Emby.Server.Implementations/Configuration/ServerConfigurationManager.cs`

| Event | EventArgs | Fires when |
|---|---|---|
| `ConfigurationUpdated` | `EventArgs` | Configuration is saved |
| `NamedConfigurationUpdating` | `ConfigurationUpdateEventArgs` | Before a named configuration is updated |
| `NamedConfigurationUpdated` | `ConfigurationUpdateEventArgs` | After a named configuration is updated |
| `ConfigurationUpdating` | `GenericEventArgs<ServerConfiguration>` | Before server configuration updates |

### `IDeviceManager`
`MediaBrowser.Controller/Devices/IDeviceManager.cs`

| Event | EventArgs | Fires when |
|---|---|---|
| `DeviceOptionsUpdated` | `GenericEventArgs<Tuple<string, DeviceOptions>>` | Device settings are modified |

### `ITaskManager` and `IScheduledTaskWorker`
`Emby.Server.Implementations/ScheduledTasks/TaskManager.cs`,
`Emby.Server.Implementations/ScheduledTasks/ScheduledTaskWorker.cs`

| Event | EventArgs | Fires when |
|---|---|---|
| `TaskManager.TaskExecuting` | `GenericEventArgs<IScheduledTaskWorker>` | A scheduled task starts |
| `TaskManager.TaskCompleted` | `TaskCompletionEventArgs` | A scheduled task finishes (success or failure) |
| `ScheduledTaskWorker.TaskProgress` | `GenericEventArgs<double>` | Per-task progress tick (0–1) |

Ideal pair for span instrumentation — start/end + duration + outcome.

### `IProviderManager`
`MediaBrowser.Controller/Providers/IProviderManager.cs`

| Event | EventArgs | Fires when |
|---|---|---|
| `RefreshStarted` | `GenericEventArgs<BaseItem>` | Metadata refresh begins for an item |
| `RefreshCompleted` | `GenericEventArgs<BaseItem>` | Metadata refresh completes |
| `RefreshProgress` | `GenericEventArgs<Tuple<BaseItem, double>>` | Periodic refresh progress (0–1) |

### `ISubtitleManager`
`MediaBrowser.Controller/Subtitles/ISubtitleManager.cs`

| Event | EventArgs | Fires when |
|---|---|---|
| `SubtitleDownloadFailure` | `SubtitleDownloadFailureEventArgs` | A subtitle download fails |

### `ILyricManager`
`MediaBrowser.Controller/Lyrics/ILyricManager.cs`

| Event | EventArgs | Fires when |
|---|---|---|
| `LyricDownloadFailure` | `LyricDownloadFailureEventArgs` | A lyric download fails |

### `ILiveTvManager`
`MediaBrowser.Controller/LiveTv/ILiveTvManager.cs`

| Event | EventArgs | Fires when |
|---|---|---|
| `TimerCreated` | `GenericEventArgs<TimerEventInfo>` | A timer is created |
| `TimerCancelled` | `GenericEventArgs<TimerEventInfo>` | A timer is cancelled |
| `SeriesTimerCreated` | `GenericEventArgs<TimerEventInfo>` | A series timer is created |
| `SeriesTimerCancelled` | `GenericEventArgs<TimerEventInfo>` | A series timer is cancelled |

### `IApplicationHost`
`MediaBrowser.Common/IApplicationHost.cs`

| Event | EventArgs | Fires when |
|---|---|---|
| `HasPendingRestartChanged` | `EventArgs` | Server pending-restart state changes |

### `CollectionFolder` (static)
`MediaBrowser.Controller/Entities/CollectionFolder.cs`

| Event | EventArgs | Fires when |
|---|---|---|
| `LibraryOptionsUpdated` | `LibraryOptionsUpdatedEventArgs` | Library settings are modified |

---

## `IEventConsumer<T>` events

Located under `MediaBrowser.Controller/Events/`. Implement
`IEventConsumer<TEventArgs>` and register it in DI; `IEventManager` dispatches
to all matching consumers when the event is published.

### Plugin lifecycle
- `PluginInstallingEventArgs` — installation has begun
- `PluginInstalledEventArgs` — installation completed
- `PluginUpdatedEventArgs` — plugin updated
- `PluginInstallationCancelledEventArgs` — installation cancelled
- `PluginUninstalledEventArgs` — plugin uninstalled

### User lifecycle
- `UserCreatedEventArgs`
- `UserDeletedEventArgs`
- `UserPasswordChangedEventArgs`
- `UserLockedOutEventArgs`

### Authentication
- `AuthenticationRequestEventArgs` — authentication attempted
- `AuthenticationResultEventArgs` — authentication result (success or failure) — high-signal for security telemetry

### Sessions (parallel to `ISessionManager`, dispatched via `IEventManager`)
- `SessionStartedEventArgs`
- `SessionEndedEventArgs`

---

## Subscription patterns

### Direct subscription

```csharp
public sealed class PlaybackInstrumentation
{
    public PlaybackInstrumentation(ISessionManager sessionManager)
    {
        sessionManager.PlaybackStart += OnPlaybackStart;
        sessionManager.PlaybackStopped += OnPlaybackStopped;
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e) { /* ... */ }
    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e) { /* ... */ }
}
```

Remember to unsubscribe in `Dispose` to avoid leaks across plugin reloads.

### `IEventConsumer<T>`

```csharp
public sealed class AuthOtelConsumer : IEventConsumer<AuthenticationResultEventArgs>
{
    public Task OnEvent(AuthenticationResultEventArgs e)
    {
        // record metric / span
        return Task.CompletedTask;
    }
}
```

Register in the plugin's `IPluginServiceRegistrator`:

```csharp
services.AddScoped<IEventConsumer<AuthenticationResultEventArgs>, AuthOtelConsumer>();
```

---

## Suggested instrumentation priorities for OTel

| Signal | Events | Why |
|---|---|---|
| Spans | `RefreshStarted`/`RefreshCompleted`, `TaskExecuting`/`TaskCompleted` | Natural start/stop pairs with duration and outcome |
| Counters | `PlaybackStart`, `PlaybackStopped`, `ItemAdded/Updated/Removed`, `AuthenticationResultEventArgs` | High-volume, useful rates |
| Gauges | `PlaybackProgress` (sampled), `RefreshProgress`, `TaskProgress` | Long-running operation visibility |
| Errors | `SubtitleDownloadFailure`, `LyricDownloadFailure`, failed `AuthenticationResultEventArgs` | Failure tracking |
