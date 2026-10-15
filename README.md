# StreamEdge

A cross-platform Twitch client for small ultrawide secondary displays, built with
[Avalonia](https://avaloniaui.net/). Chat, the stream, and your live follows sit
side by side in one short row.

![StreamEdge at 1920×480](docs/demo-1920x480.png)

> Early days. The player is tuned for low latency: it speeds up to catch up to
> live, jumps ahead after a big lag, and drops quality rather than buffering.
> See [docs/PLAN.md](docs/PLAN.md).

## Build and run

Requires the .NET 8 SDK (or newer) and libmpv for video:

- **Windows:** `powershell -File scripts/fetch-libmpv.ps1` (once; the build copies it next to the app)
- **Linux:** `sudo apt install libmpv2` (or your distribution's mpv library package)
- **macOS:** `brew install mpv`

Without libmpv everything else works and the player pane says what's missing.

```sh
dotnet build StreamEdge.sln
dotnet run --project app/StreamEdge.App.Avalonia -- --demo          # sample data, no network
dotnet run --project app/StreamEdge.App.Avalonia -- --channel somechannel --screen 1 --fullscreen
dotnet test tests/managed/StreamEdge.Tests.csproj
dotnet test tests/ui/StreamEdge.UiTests.csproj
```

In VS Code, install the recommended **Task Buttons** extension to get
Build & Run / Demo / Test / Release buttons in the status bar.

## Signing in

Click **Log in with Twitch** (in the empty stream list, or under ⚙). Twitch's
sign-in page opens in your browser; log in, click **Authorize**, and StreamEdge
picks it up automatically.

StreamEdge ships with its own Twitch app ID. Forks can register their own app
at <https://dev.twitch.tv/console/apps>. It must use **Client Type: Public**, and
the redirect URL is unused (`http://localhost` is fine). Then either change
`TwitchApp.ClientId` or set `ClientId` in settings.json.

**Ad-free playback (optional):** if you're subscribed to a channel or have Turbo,
paste your browser's twitch.tv `auth-token` cookie into the same flyout. It is
stored encrypted (DPAPI on Windows) and sent only to `gql.twitch.tv`.

## Release

`pwsh scripts/build-release.ps1` (or `powershell -File …` on Windows) publishes a self-contained single-file build for
the host OS into `dist/`. Pushing a `v*` tag runs `.github/workflows/release.yml`
for Windows, Linux (x64 and arm64) and macOS.

## License

GPL-3.0-or-later
