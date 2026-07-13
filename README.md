# ARK Join Assist

Current app version: **0.12.0**

A Windows menu helper for ARK: Survival Ascended. Click **GO** or press the global **Ctrl+G** shortcut once and the helper repeatedly tries ASA's last-played server using only the game's visible menus. Press **Ctrl+G** again to stop from anywhere.

No server name, address, API key, console command, or manual menu navigation is required. Choose the desired server in ASA once; subsequent runs use ASA's own last-played selection.

## Download and run

No compilation and no separate .NET installation are required.

1. Download [ARK-Join-Assist-win-x64.zip](https://github.com/jonschr/ark-fix-your-queues/releases/latest/download/ARK-Join-Assist-win-x64.zip) from the latest GitHub Release.
2. Extract the entire ZIP to a normal writable folder, such as `Documents\ARK Join Assist`. Do not run the executable from inside the ZIP.
3. Run `ArkFixYourQueues.exe`.
4. If Windows SmartScreen appears, confirm that the publisher is unknown and choose **Run anyway**. GitHub Release builds are not currently code-signed.

The download is self-contained for 64-bit Windows. Keep the extracted files together; the application executable depends on the other packaged files.

### Automatic updates

The app checks the latest GitHub Release in the background. When a newer version is found, it downloads both the Windows ZIP and its published SHA-256 checksum, verifies the package, and records that the update is ready in the activity log. The app continues running normally.

The verified update installs automatically only after you close the app. A small standalone updater waits for the main process to exit, replaces the installation files, and restarts the new version. Local settings and delay-performance history are stored under `%LOCALAPPDATA%\ArkFixYourQueues` and are not replaced by updates.

## Live workflow

The helper recognizes the current ASA menu and takes the shortest available path:

1. On the startup screen, press **Start**, then click **JOIN GAME**.
2. If ASA reports that the server is full, click **CANCEL**. If ASA reports **JOINING FAILED**, click **OK**.
3. Click **BACK**, then **JOIN GAME**, then **JOIN LAST PLAYED**.
4. Repeat until ASA stops returning a failure dialog.
5. Stop all input when joining/loading appears to continue.

Screen state is checked four times per second. The configured attempt spacing controls when the helper cancels ASA's non-authoritative “Joining server...” overlay and begins another cycle; a confirmed loading globe suspends retry navigation.

## Safety boundaries

- Automation is limited to ASA's visible startup and multiplayer menus.
- The helper reads no game memory, injects no code, manipulates no packets, and does not alter BattlEye or game files.
- Unknown screens, capture failure, or the ASA process closing stop the workflow safely.
- After an attempt, the helper treats ASA's “Joining server...” text as a transient overlay rather than proof of loading. The configured spacing controls cancellation, while recognized failure dialogs and loading globes take priority.
- If no recognized workflow transition occurs for five seconds while running, the helper plays one Windows attention sound and records it in the activity log. Progress re-arms the alert for the next inactive period.
- **Attempt spacing** enforces 0–300 seconds between every actual join click, including retries after menu recovery. It defaults to 5 seconds.
- The latest ASA capture is shown full-width above the activity log and preserves the ASA window's original aspect ratio.
- Recognized error dialogs and confirmed purple-globe loading screens are retained as in-memory screenshot cards in a horizontal session banner, newest first. A globe starts a monitored loading attempt but does not stop the workflow because ASA may still return a post-load server-full error. Ordinary session-browser “loading/joining” messages are not captured. The banner is capped at 60 cards and cleared completely when the app closes; no evidence screenshots are written to disk.
- The activity summary shows attempts, average seconds per attempt, and loading globes. Loading-globe yield is tracked per configured attempt spacing, persisted across launches, and ranked in a compact all-time delay-performance summary.
- Clicking any session-evidence thumbnail opens a large native-aspect preview.
- Clicks and classifier regions are mapped through ASA's centered 16:9 safe UI area, improving compatibility between 16:9, ultrawide, windowed, and differently positioned game windows.
- One second before the inactivity alert, an optional recovery watchdog checks for known OK/Cancel dialogs or an unexpected return to the startup/main menu and takes only positively recognized actions. It repeats every 5 seconds until the workflow progresses, defaults on, and can be disabled before starting.
- The helper performs no gameplay automation.

## Build from source

Maintainers can rebuild with the .NET 8 SDK:

```powershell
dotnet restore
dotnet test -c Release
dotnet publish src\ArkFixYourQueues\ArkFixYourQueues.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
```

Pushing a tag such as `v0.12.0` runs `.github/workflows/release.yml`, tests the project, creates the self-contained Windows ZIP and checksum, and publishes both to a GitHub Release. The main WPF application intentionally remains a folder deployment because its earlier single-file build hung on this PC; only the small updater is published as a standalone single file.
