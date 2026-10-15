# StreamEdge plan

A cross-platform (Windows / Linux / macOS) Twitch client built for the **small,
very wide secondary display**: the 1920×480 / 1920×440 bar panels, a 2560×720
strip, or a short window under the main monitor. Watch one stream, read its chat,
and keep an eye on who's live, all in a single row.

![Demo layout at 1920×480](demo-1920x480.png)

## Layout

```
┌──────────── chat ────────────┬────────── player ──────────┬──────── live streams ────────┐
│ #channel           Connected │                            │ Live Streams (33) [Viewers] ⇅⚙│
│ 7:13 PM ### history ###      │                            │ ──── ★ GlitchCat7 (57|2.4h) ──│
│ 7:14 PM [B][S] Name: msg …   │     16:9 video, exactly    │ Look out Streamer, Deadlines! │
│ 7:14 PM JOIN: someone        │     window-height tall     │ (Super Mario World)           │
│ …                            │                            │ ──── Jerma985 (11,455|2.7h) ──│
│ [ Send a message           ] │                            │ …                             │
└──────────────────────────────┴────────────────────────────┴───────────────────────────────┘
          ChatShare × rest           height × 16/9                (1 − ChatShare) × rest
```

- **The player's width follows the window height** (`UltrawideLayout`), so the
  video fills the display top to bottom with no letterboxing. Chat and the list
  share what is left. On a 1920×480 panel that's 853 px of video and ~533 px per side.
- **No menu bar or title chrome.** Each pane gets a 24 px header strip; settings
  live in a flyout (⚙). Vertical pixels are the scarce resource.
- Too narrow for both sides at `MinSideWidth`? The player shrinks (letterboxes);
  the sides never disappear. The player can be hidden to give chat and list everything.
- Visual language follows Chatty (the reference screenshot): alternating chat
  rows, `h:mm tt` timestamps, badges before coloured names, underlined links, and
  centred `──── Name (viewers | uptime) ────` list headers with the title and
  `(game)` beneath. It uses a dark Twitch palette instead of Chatty's light list.

## Architecture

Same shape as MeshRF:

| Project | Role |
|---|---|
| `app/StreamEdge.Core` | UI-agnostic: IRC parser, chat client, Helix, OAuth, GQL playback, settings, secrets, formatting. Fully unit-tested. |
| `app/StreamEdge.App.Avalonia` | Avalonia 12 + CommunityToolkit.Mvvm. Views, view models, `UltrawideLayout`. |
| `tests/managed` | xunit v2 tests for Core. |
| `tests/ui` | Headless Avalonia + Skia. Renders the real window (`STREAMEDGE_SNAPSHOT=out.png` saves it). |

Data lives in `AppData.Directory` (`%APPDATA%\StreamEdge`, `~/.config/StreamEdge`,
`~/Library/Application Support/StreamEdge`): `settings.json` plus `secrets/`.

## Accounts and tokens

There are two independent credentials, and both are optional:

| | OAuth sign-in | Playback `auth-token` |
|---|---|---|
| What | Helix user token via **Device Code Grant** (`TwitchAuth`) | The `auth-token` cookie from a browser signed in to twitch.tv |
| Enables | Followed-live list, sending chat, (later) badges/emotes for the user | **Ad-free HLS** for channels you're subbed to, or everywhere with Turbo |
| Sent to | `id.twitch.tv`, `api.twitch.tv`, IRC | `gql.twitch.tv` only, as `Authorization: OAuth …` on the PlaybackAccessToken query |
| Why separate | GQL doesn't accept third-party Helix tokens, and the web client ID GQL needs can't be used for OAuth | |

- Login UX: **Log in with Twitch** opens Twitch's own sign-in page in the default
  browser, with the device code pre-filled in the URL. The user logs in and clicks
  Authorize, and the app polls for the token, so no code needs typing. The code is
  shown small as a fallback. An in-app username/password form isn't an option,
  because Twitch requires its own login page.
- Under the hood this is the device code flow: no embedded browser, redirect URI
  listener or client secret, so it behaves the same on all three OSes. The app
  ships its own **Public** client ID (`TwitchApp.ClientId`). Public is what lets
  tokens refresh without a secret. `ClientId` in settings.json overrides it for forks.
- Tokens are validated at start-up (Twitch requires it) and refreshed with the
  refresh token when they expire. Hourly re-validation is still TODO.
- **Secrets never go in settings.json.** `SecretStore` uses DPAPI (current user)
  on Windows and 0600 files elsewhere. Moving to Keychain and libsecret is in phase 5.
- Without either credential, chat is read anonymously (`justinfan…`) and
  playback is the normal ad-supported stream.

## Roadmap

### Phase 0: scaffold ✅
Repo layout, build/test tasks, CI, release script, three-pane layout, demo mode,
anonymous IRC chat, device-code sign-in, Helix followed streams, GQL + usher
playlist resolution (with optional auth-token), secrets store.

### Phase 1: chat that reads like official Twitch
- [x] Twitch emotes from the `emotes` tag (code-point offsets), animated GIF/WebP decoded with SkiaSharp, cached on disk, one shared animation clock
- [x] Badges from Helix (signed in) with coloured placeholder chips otherwise; FFZ user badges and custom mod/VIP art
- [x] Twitch styling: reply context, highlighted / first-time / mention / sub-and-raid rows, paid message effects, gigantified emotes, deleted messages and timeouts (CLEARMSG/CLEARCHAT); JOIN/PART off by default
- [x] Optional, each switchable in ⚙: pronouns (alejo.io), BTTV, FFZ, 7TV (zero-width emotes stacked on the emote before them)
- [x] Own messages drawn from USERSTATE (badges, colour) with the user's Twitch emotes (Helix, `user:read:emotes`)
- [x] Emote menu like twitch.tv's: frequently used, this channel, the user's other channels, Twitch globals, 7TV/BTTV/FFZ sets, search
- [ ] 7TV cosmetics (name paints, badges): need its EventAPI websocket
- [ ] Cheermotes (bits are shown as a count for now)
- [ ] Recent-message history on join (recent-messages.robotty.de), ended with "### Finished with history logs. ###"
- [ ] Mention and highlight rules, name click → user card flyout
- [ ] Virtualised list for busy channels and a "scroll to resume" pill when paused
- [ ] Clickable links (open in the default browser)

### Phase 2: stream list
- [ ] Favourite toggle (context menu) → `Favorites`
- [ ] Go-live notifications (OS toast) for favourites
- [ ] Hourly token re-validation, plus 401 → refresh → retry in Helix calls
- [ ] Remember the selected channel. The list is followed-only; add a search box for others

### Phase 3: video (latency first)

The player is built for **being close to live and catching back up after a
lag**, ahead of picture quality.

```
Twitch CDN ──HLS + prefetch──▶ LiveHlsReader ──TS bytes──▶ LocalStreamServer (127.0.0.1) ──▶ mpv (libmpv, wid)
                                    ▲                                                          │
                                    └──── LivePlaybackSession ◀── CatchUpController ◀── buffer/stall stats (4 Hz)
```

- **mpv over VLC:** mpv exposes buffered time (`demuxer-cache-duration`) and
  stall state (`paused-for-cache`), and changes `speed` live with
  pitch-corrected audio. Catch-up depends on all three. It runs with
  `profile=low-latency`, no probing, and frame dropping instead of falling behind.
- **The app fetches HLS itself, not mpv:** standard HLS clients wait for whole
  segments and ignore Twitch's `#EXT-X-TWITCH-PREFETCH`. `LiveHlsReader` joins at
  the newest complete segment, streams prefetch segments while they are still
  being encoded, and forwards bytes as they arrive. The player reads them from a
  one-client local HTTP server; reloading that URL is how a resync takes effect.
- **Catch-up rules (`CatchUpController`, unit-tested):**
  - Buffer above 2 s: play faster, proportionally, up to 1.25×, until back at the 1 s target.
  - Buffer above 6 s, or the downloader 3 or more segments behind: drop the buffer and rejoin at the live edge (8 s cooldown).
  - 2 stalls within 45 s: drop one quality step. The stream never drops to audio-only on its own.
  - 3 stall-free minutes after a drop: try one step back up, never above the preference.
  - Stalls during ad breaks don't count.
- Ads play, as on twitch.tv, with an "Ad break" label on the video (none at all with the `auth-token` and a sub or Turbo). Skipping them (`SkipAds`, off) left the player's connection silent, and mpv drops a silent connection after ~30 s, mid pre-roll, which reconnected into a fresh pre-roll.
- Quality changes between video variants are seamless: the reader switches playlists at the next segment on the same connection (Twitch variants share segment numbers and timestamps), so there's no reload or rebuffer.
- Recovery: every network step has a timeout; a stream with no new bytes for 12 s (outside ad breaks) reconnects with a fresh token; any resolve failure, timeouts included, schedules a retry; ⟳ retries offline tabs at once.
- Closed captions (CEA-608 in the video) via mpv's caption track, toggled with CC; front tab only.
- Offline channels show their offline banner (or avatar), from GQL.
- Token expiry or a dropped stream: the player goes idle, and the session re-resolves with backoff. Offline channels are re-checked every 30 s.
- Controls sit in the chat header (quality menu, mute, latency, catch-up speed),
  because nothing can be drawn over a native video window. Transient notices use mpv's OSD.
- libmpv: on Windows `scripts/fetch-libmpv.ps1` provides a pinned build, which ships beside the exe.
  On Linux and macOS it comes from the system package (`libmpv2`, `brew install mpv`).

Still to do:
- [ ] Verify embedding on Linux (X11; Wayland needs XWayland for `wid`) and macOS
- [ ] Volume slider (mute and volume are wired, but there's no slider yet)
- [ ] Show the catch-up thresholds in settings for people who prefer smoothness
- Risks: Twitch can add Client-Integrity requirements to GQL at any time, and the web client ID and query shape are undocumented. Keep all of it in `TwitchPlayback` so a fix is one file

### Phase 4: ultrawide polish
- [x] Draggable grips beside the player: the video keeps its full-height 16:9 size and slides, one side growing by what the other loses. Saved as a share, so it scales with the window; double-click to re-center
- [ ] Player show/hide toggle
- [ ] Screen picker in the flyout (`ScreenIndex`), borderless/fullscreen toggle, always-on-top
- [ ] Font-size scale for tiny high-DPI bars, and layout presets (1920×480, 1920×440, 2560×720, 3840×1100)
- [x] Channel tabs: double-click opens, right-click opens in a new or background tab; background tabs keep playing as audio only, or as PIP thumbnails over the player (lowest quality); selecting a tab brings it forward
- [ ] PIP z-order on Linux/macOS (Windows raises thumbnails explicitly); clicking a PIP thumbnail to bring it forward

### Phase 5: packaging
- [ ] App icon, macOS `.app` bundle and signing notes, Linux `.desktop` entry
- [ ] Keychain (macOS) and libsecret (Linux) behind `SecretStore`
- [ ] Single-instance guard (MeshRF's `SingleInstance` pattern)

## Command line

```
StreamEdge [--demo] [--channel <login>] [--screen <index>] [--fullscreen]
```
These override settings for one run and are never written back.
