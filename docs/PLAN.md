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
│ 7:13 PM ### history ###      │                            │ ──── ★ CometCrumb (57|2.4h) ──│
│ 7:14 PM [B][S] Name: msg …   │     16:9 video, exactly    │ Look out Streamer, Deadlines! │
│ 7:14 PM JOIN: someone        │     window-height tall     │ (Super Mario World)           │
│ …                            │                            │ BigTimeBramble (11,455|2.7h) ─│
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
- [x] Name popups (viewer cards): avatar, account age (GQL, signed out), follow age (Helix, moderators only), badges, recent messages, mention / open channel
- [x] Moderation via Helix: per-line delete / 10-minute timeout / ban icons, timeouts / ban / unban / warn with reason in the card, and a 🛡 menu for chat modes, shield mode and announcements
- [x] Chat history on open: Twitch's recentChatMessages (auth-token), else recent-messages.robotty.de
- [x] App icon (icon.png → scripts/make-icon.ps1)
- [ ] 7TV cosmetics (name paints, badges): need its EventAPI websocket
- [x] Cheermotes: Helix "Get Cheermotes" per channel (signed in), the tier picture and amount in its colour
- [x] Recent-message history on join (see "Chat history on open" above)
- [x] Name click → user card flyout
- [ ] Mention and highlight rules
- [ ] Virtualised list for busy channels and a "scroll to resume" pill when paused
- [x] Clickable links (open in the default browser)
- [x] Replies: ↩ on a line (hover), sent with IRC's reply tag, a "Replying to" bar above the box
- [x] Slash commands through Helix (IRC only runs /me since 2023): /ban, /timeout, /warn, chat modes, /announce, /shield, /color, /block, /w; /help lists them
- [x] Whispers: sent with Helix (needs a verified phone), received over EventSub `user.whisper.message`; kept in the ✉ inbox in the chat header (not the chats), one conversation per person, saved to whispers.json since Twitch has no history API; /r answers

### Phase 2: stream list
- [x] Favourite toggle (context menu) → `Favorites`
- [x] Go-live alerts for favourites, inside the window (top right), from the followed-live refresh; OS toasts still to do
- [x] "◌ Offline" toggle: followed channels that are offline listed below the live ones, most recently live first (Helix followed channels + GQL `users.lastBroadcast`, both every 10 min); offline rows and offline tabs say "Last live 3h ago · Game" on hover
- [x] Hover card: content classification labels (Helix "Get Channel Information") and tags
- Pinned messages: no EventSub type exists, and Helix `GET /chat/pins` needs a moderator, so viewers can't see pins through the official API (checked Oct 2026)
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
- **Catch-up rules (`CatchUpController`, unit-tested), tuned against real runs:**
  - Decisions use a smoothed buffer (4 s time constant): segments arrive in bursts, so the raw buffer swings by ~2 s every segment, and reacting to that swing was the stutter.
  - Target 2 s; play 1.08× above 4 s, 1.15× when well behind, back to 1× near the target; never within 30 s of a stall.
  - Above 10 s, or the downloader 4+ segments behind: rejoin at the live edge (15 s cooldown).
  - Bandwidth: if less than 0.85 s of video arrives per second (over 8 s), jump straight to the highest quality that fits. Otherwise 3 stalls in 60 s drop one step; stalls in the first 10 s after a load and during ads don't count. Three stall-free minutes: one step back up.
  - mpv starts and resumes after 1 s buffered (not on the first frames), with a 100 ms audio buffer.
- Twitch serves MPEG-TS on some channels and fragmented MP4 on others (`#EXT-X-MAP` init segment): the reader sends the init segment first, mpv detects the format, and MP4 quality changes reload (each quality has its own init segment).
- Ads play, as on twitch.tv, with an "Ad break" label on the video (none at all with the `auth-token` and a sub or Turbo). There is no way to skip them: Twitch's developer agreement forbids it.
- Quality changes between video variants are seamless: the reader switches playlists at the next segment on the same connection (Twitch variants share segment numbers and timestamps), so there's no reload or rebuffer.
- Recovery: every network step has a timeout; a stream with no new bytes for 12 s, or whose playback position hasn't moved for 20 s (outside ad breaks), reconnects with a fresh token; a reader that fell off the playlist (an outage) reloads at the live edge rather than splice a jump in time; any resolve failure, timeouts included, schedules a retry; ⟳ retries offline tabs at once. With PIP off, background tabs stop entirely until brought forward.
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
- [x] Docking: chat, the live list and the About panels (GQL `user.panels`) each dock left of, right of, above or below the video by dragging their header onto a side of it (the videos step aside during the drag so the targets can be drawn), saved where left. Rows above and below shorten the video, which narrows to stay 16:9; panes on one side share it. The list and panels show or hide from ▦ by PIP. Replaces the 4:3 "video above the list" layout and the chat-on-the-left switch (older settings keep their side)
- [x] Resizing everything but the video: drag the gaps between columns on a side, between stacked panes, and between a row above or below the video and the video. Sizes are weights on the pane places (and a share per row), saved; moving a pane evens out the sides it left and joined
- [x] Settings live under ⚙ beside PIP, as cards of switches with a line each
- [ ] Player show/hide toggle
- [ ] Screen picker in the flyout (`ScreenIndex`), borderless/fullscreen toggle, always-on-top
- [ ] Font-size scale for tiny high-DPI bars, and layout presets (1920×480, 1920×440, 2560×720, 3840×1100)
- [x] Channel tabs: double-click opens, right-click opens in a new or background tab; background tabs keep playing as PIP thumbnails over the player (lowest quality), or pause with PIP off; selecting a tab brings it forward
- [x] Clicking a PIP thumbnail brings it forward; dragging snaps it to a 4×4 grid spread corner to corner, remembered per channel (Windows); 17 channels at most
- [ ] PIP z-order and dragging on Linux/macOS (Windows raises thumbnails explicitly)

### Phase 5: packaging
- [ ] App icon, macOS `.app` bundle and signing notes, Linux `.desktop` entry
- [ ] Keychain (macOS) and libsecret (Linux) behind `SecretStore`
- [ ] Single-instance guard (MeshRF's `SingleInstance` pattern)

## Command line

```
StreamEdge [--demo] [--channel <login>] [--screen <index>] [--fullscreen]
```
These override settings for one run and are never written back.
