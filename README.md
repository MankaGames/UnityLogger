# MankaLogHandler

File-based Unity log handler with duplicate filtering, automatic log rotation, and crash report export.

## Features

- Writes all Unity logs to a persistent file (`<productName>.log`)
- Deduplicates repeated messages (marks them with `*`)
- Rotates the log file when it exceeds 10 MB
- Dumps device/system info header on first launch
- Resets the log file on app version change
- `GetLogForReport()` — returns the last 200 KB of the log for in-game bug reports

## Installation

Add to your project's `Packages/manifest.json`:

```json
"com.manka.loghandler": "https://github.com/MankaGames/UnityLogger.git"
```

To pin a specific commit or tag:

```json
"com.manka.loghandler": "https://github.com/MankaGames/UnityLogger.git#v1.0.0"
```

## Usage

The handler must be instantiated once and kept alive for the duration of the app.
The recommended approach is a static initializer with `RuntimeInitializeOnLoadMethod`:

```csharp
using MankaGames;
using UnityEngine;

public static class LogHandlerInit
{
    private static MankaLogHandler _handler;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Init()
    {
        // Pass any PlayerPrefs keys you want dumped in the system-info header.
        // Pass null (or omit the argument) if you don't need this.
        _handler = new MankaLogHandler(new[]
        {
            "Language",
            "MasterVol",
            "MusicVol",
        });

        Application.quitting += OnQuit;
    }

    private static void OnQuit()
    {
        _handler?.FlushBuffer();
        _handler?.Dispose();
        _handler = null;
    }
}
```

Place this file anywhere under `Assets/Scripts/` — it is project-specific and should **not** live inside the package itself.

## Log file location

`Application.persistentDataPath/<productName>.log`

| Platform  | Typical path |
|-----------|--------------|
| Windows   | `%APPDATA%\..\LocalLow\<company>\<product>\` |
| Android   | `/storage/emulated/0/Android/data/<bundle>/files/` |
| iOS       | `<app>/Documents/` |

## Bug report integration

```csharp
string report = LogHandlerInit.Handler?.GetLogForReport() ?? "no log";
// send `report` via your feedback system
```

Expose `_handler` as a public property if you need to call `GetLogForReport()` from other systems.

## API

| Member | Description |
|--------|-------------|
| `MankaLogHandler(string[] prefsKeys = null)` | Constructor. Installs itself as `Debug.unityLogger.logHandler`. |
| `FlushBuffer()` | Writes buffered entries to disk immediately. |
| `GetLogForReport()` | Returns the full log (or last 200 KB if large) as a string. |
| `Dispose()` | Restores the original log handler and closes the file stream. |
