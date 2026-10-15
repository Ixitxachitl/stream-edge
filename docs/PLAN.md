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

- Device code flow: the app shows a code, and the user approves it at
  twitch.tv/activate on any device. It needs no embedded browser, redirect URI or
  client secret, so it works the same on all three OSes. This needs a Twitch
  application registered as a **Public** client, whose Client ID goes in the flyout
  (stored as `ClientId` in settings.json).
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

### Phase 1: chat that reads like Chatty
- [ ] Emotes: Twitch (from the `emotes` tag) plus BTTV / FFZ / 7TV, as `InlineUIContainer` images with a disk cache
- [ ] Real badge images (Helix `chat/badges` + `chat/badges/global`)
- [ ] Pronouns next to names (pronouns.alejo.io), as in the reference
- [ ] Recent-message history on join (recent-messages.robotty.de), ended with "### Finished with history logs. ###"
- [ ] Mention and highlight rules, name click → user card flyout
- [ ] Virtualised list for busy channels and a "scroll to resume" pill when paused
- [ ] Clickable links (open in the default browser)

### Phase 2: stream list
- [ ] Favourite toggle (context menu) → `Favorites`
- [ ] Go-live notifications (OS toast) for favourites
- [ ] Hourly token re-validation, plus 401 → refresh → retry in Helix calls
- [ ] Remember the selected channel. The list is followed-only; add a search box for others

### Phase 3: video
- [ ] **Spike first:** confirm LibVLCSharp.Avalonia (or a `NativeControlHost` hosting a VLC/mpv window) works on Avalonia 12 on all three OSes. Fallback: libmpv render API into an `OpenGlControlBase`
- [ ] Play `Player.Playlist` (already resolved), with a quality picker parsed from the master playlist, audio-only mode, volume and mute overlay
- [ ] Low-latency HLS tuning and auto-reconnect when the stream drops or the token expires
- [ ] Ad handling for anonymous viewers: show a "commercial break" overlay rather than stalling
- Risks: Twitch can add Client-Integrity requirements to GQL at any time, and the web client ID and query shape are undocumented. Keep all of it in `TwitchPlayback` so a fix is one file

### Phase 4: ultrawide polish
- [ ] Draggable splitters that write `ChatShare`, and a player show/hide toggle
- [ ] Screen picker in the flyout (`ScreenIndex`), borderless/fullscreen toggle, always-on-top
- [ ] Font-size scale for tiny high-DPI bars, and layout presets (1920×480, 1920×440, 2560×720, 3840×1100)
- [ ] Multi-channel chat tabs in the chat pane

### Phase 5: packaging
- [ ] App icon, macOS `.app` bundle and signing notes, Linux `.desktop` entry
- [ ] Keychain (macOS) and libsecret (Linux) behind `SecretStore`
- [ ] Single-instance guard (MeshRF's `SingleInstance` pattern)

## Command line

```
StreamEdge [--demo] [--channel <login>] [--screen <index>] [--fullscreen]
```
These override settings for one run and are never written back.
