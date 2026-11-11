# Muse Desktop

A Windows desktop shell for [muse.ai](https://muse.ai) with one job the web app
cannot do on its own: **getting your screen into the conversation**. Muse
Desktop wraps the Muse web app in a native window and adds one-click screen
capture (a region "lens", full monitors), screen recording, and fully
assignable global hotkeys — every capture lands directly in the chat composer
as a pasted attachment, ready to send.

Current version: **1.6.4** · Platform: **Windows 10/11 (x64)** · UI language of
the installer and in-app strings: Vietnamese.

---

## Features

- **Lens capture** — press the Lens button (or its hotkey), drag a rectangle
  over anything on any monitor, release: the region is captured and pasted
  straight into the chat composer. The selector uses the lens mechanics of
  [WinLens](https://github.com/marco-beltrame/WinLens) (see *Credits*):
  per-monitor DPI-v2 coordinates in physical pixels, the window pinned by
  `SetWindowPos` so mixed-DPI multi-monitor setups map 1:1, four dimming
  rectangles around a clear selection, and a live size chip in physical
  pixels.
- **Full-monitor capture** — one button (or hotkey) per monitor, captured and
  pasted the same way.
- **Screen recording** — Record 1 / Record 2 toggle buttons. First press
  starts recording that monitor, second press stops, finalises an MP4 and
  pastes it into the composer as a file attachment. Recording is done by a
  bundled `ffmpeg` (gdigrab → libx264, 30 fps, no audio track).
- **Global hotkeys for everything** — lens, each monitor capture, each
  recording toggle, plus a fixed Alt+Space show/hide. Every capture/record
  key can be rebound to *any* key or combination (right-click a toolbar
  button, or use the tray menu → "Gán phím chụp…"). Bindings persist in
  `hotkeys.json` and take effect the moment the dialog closes.
- **In-page right-click menu** — right-clicking the chat page opens the app's
  own dark context menu (capture, record, hotkey assignment, hide/show
  toolbar, reload) instead of the browser's.
- **Hideable chrome** — hide the toolbar and status bar and the chat fills
  the entire window; bring them back from the right-click menu or the tray.
- **Tray resident** — closing the window parks the app in the system tray;
  Alt+Space toggles it from anywhere. Optional start-with-Windows.
- **Spell-check off** — the embedded Chromium spell checker only ships a
  dictionary for its UI language and underlines Vietnamese text in red, so
  it is disabled both at the browser level and per editable element.
- **Dark title bar**, single-instance behaviour (a second launch raises the
  running window instead of starting a twin), and a crash log that records
  failures instead of letting them vanish silently.

## How it works

Muse Desktop is a thin native host around the real web app. It never
reimplements chat: the window contains a WebView2 control pointed at
`https://muse.ai`, and everything else is plumbing between the host, the
operating system, and a small script injected into the page.

```
┌──────────────────────────────────────────────────────────────┐
│ MainWindow (WPF)                                             │
│  ├─ Toolbar: Lens / Screen 1 / Screen 2 / Rec 1 / Rec 2      │
│  ├─ Status bar (stage narrator + recording timer)            │
│  ├─ WebView2 ─────────────── https://muse.ai                 │
│  │     ▲ WebMessageReceived        │ AddScriptToExecute…     │
│  │     └────────── bridge script (in-page) ──────────┐       │
│  ├─ WH_KEYBOARD_LL hook → matches live hotkey bindings       │
│  ├─ Recorder → ffmpeg (gdigrab) → MP4 → clipboard file drop  │
│  └─ Tray icon + context menu, single-instance mutex          │
└──────────────────────────────────────────────────────────────┘
```

### The in-page bridge

A small JavaScript bridge is injected into every document (and frame) via
`AddScriptToExecuteOnDocumentCreatedAsync`. It does exactly four things:

1. **Focus the composer** on request (`__museFocusComposer`): finds the
   lowest visible `[contenteditable]` / `[role="textbox"]` / `textarea`
   (piercing shadow roots) — the host calls this right before pasting, so
   captures land in the input box instead of nowhere.
2. **Report right-clicks**: a capture-phase `contextmenu` listener cancels
   the browser menu and posts `muse-contextmenu` to the host, which draws
   its own WPF context menu at the cursor (the same popup technology as
   the tray menu).
3. **Kill spell-checking**: forces `spellcheck=false` on every editable
   element, with a `MutationObserver` that re-applies it as the SPA
   creates new nodes.
4. Nothing else. No reading of message content, no interception of Enter
   or of the Send button — an earlier design did that and was removed
   wholesale; sending is plain web behaviour.

Communication is one-way JSON strings via `window.chrome.webview.postMessage`
→ `CoreWebView2.WebMessageReceived` on the host.

### Capture flow (image)

1. The host hides its own window (so it never photographs itself), waits
   briefly for the desktop to settle.
2. For the lens, `RegionWindow` shows the WinLens-style selector pinned in
   physical pixels; on release the rectangle is final. For full monitors,
   the target is that monitor's bounds from `System.Windows.Forms.Screen`.
3. The region is copied with GDI (`Capture.cs`), placed on the clipboard
   as a bitmap, the window is shown and focused again, the bridge focuses
   the composer, and the host synthesises Ctrl+V. The image arrives in the
   composer as a native attachment.

### Recording flow (video)

`Recorder.cs` launches the bundled ffmpeg with the monitor's desktop
coordinates:

```
ffmpeg -f gdigrab -framerate 30 -offset_x <x> -offset_y <y> \
       -video_size <w>x<h> -i desktop -c:v libx264 -preset ultrafast \
       -pix_fmt yuv420p <file>.mp4
```

Stopping writes `q` to ffmpeg's stdin so the MP4 tail is finalised
gracefully. The file is kept in `%LocalAppData%\MuseDesktop\recordings`
and also put on the clipboard as a file drop, then pasted into the
composer with the same focus-then-Ctrl+V path as images.

### Hotkey engine — why a keyboard hook, not `RegisterHotKey`

Versions up to 1.6.3 used Win32 `RegisterHotKey`. On the target machine,
bindings saved correctly and re-registration reported no error the user
ever saw, yet `WM_HOTKEY` simply never arrived — a silent dead layer.
1.6.4 replaced the mechanism instead of patching it again: a
`WH_KEYBOARD_LL` low-level hook observes every key press system-wide,
matches it against the *live* bindings (virtual-key code + modifier state
from `GetAsyncKeyState`), fires the action on the UI thread via the
dispatcher, and swallows matched keys so a bare-letter binding doesn't
also type into the foreground app. There is no per-key registration step
left to fail: the hook either installs at startup (a failure is written
to `crash.log`) or every binding works. While the assignment dialog is
open the hook is suspended by a flag, so the key being captured is
neither fired nor swallowed behind the dialog's back. Auto-repeat is
suppressed by tracking the fired key until its key-up.

### Spell-check, in two layers

1. The WebView2 environment is created with
   `AdditionalBrowserArguments = "--disable-spell-checking"`.
2. The bridge forces `spellcheck=false` on editables (above) in case the
   switch is ever ignored. The observer targets the `Document` node
   itself — at document-creation time `document.documentElement` may not
   exist yet, which silently killed an earlier version of this code; a
   lab test now guards that behaviour (see *Testing*).

## Configuration & data locations

All state lives under `%LocalAppData%\MuseDesktop\`:

| Path | Contents |
| --- | --- |
| `WebView2Data\` | WebView2 profile: cookies, login session, cache |
| `hotkeys.json` | The five assignable bindings (`{Mods, Vk}` per action) |
| `recordings\` | Finished MP4 files from screen recordings |
| `crash.log` | Append-only diagnostics: hook install failures, bridge events, unhandled exceptions |

Install location: `%LocalAppData%\Programs\MuseDesktop\` (per-user, no
admin rights). Autostart, when enabled from the tray, is a `Run` registry
value named `MuseDesktop` under `HKCU`.

Default bindings: Lens `Ctrl+Alt+V`, Screen 1 `Ctrl+Alt+1`, Screen 2
`Ctrl+Alt+2`, recordings unassigned, show/hide `Alt+Space` (fixed).

**Warning about bare keys:** any single key may be bound, but a bare
letter bound globally is swallowed system-wide while the app runs —
typing that letter into other programs will trigger the capture instead.
That is a deliberate, user-chosen trade-off, and the assignment dialog
says so.

## Building from source

### Prerequisites

- Windows 10/11 x64 (the build itself cross-compiles from Linux too —
  this project is built on Linux with `EnableWindowsTargeting`).
- .NET SDK 8 or newer (`net8.0-windows`, WPF, self-contained win-x64).
- [NSIS](https://nsis.sourceforge.io/) 3.x for the installer
  (`makensis installer.nsi`).
- Two runtime payloads that are **not** in this repository:
  - `tools\ffmpeg.exe` — a Windows ffmpeg build with `gdigrab` and
    `libx264` (the builds from the *ffmpeg-static* project work; ~83 MB).
    Place it at `tools\ffmpeg.exe`; the packaging step copies it into
    the staging folder.
  - `MicrosoftEdgeWebView2Setup.exe` — Microsoft's WebView2 runtime
    bootstrapper, placed next to `installer.nsi`. The installer only
    runs it when the WebView2 runtime is genuinely missing.

### Build & package

```powershell
# 1. Publish the app (self-contained)
dotnet publish src\MuseDesktop.csproj -c Release -r win-x64 `
    --self-contained true -o publish

# 2. Stage everything the installer ships
mkdir staging2
copy publish\* staging2\
copy pack\HUONG_DAN.txt staging2\
copy MicrosoftEdgeWebView2Setup.exe staging2\
copy tools\ffmpeg.exe staging2\

# 3. Build the installer
makensis installer.nsi
# → MuseDesktop-Setup-NSIS.exe (per-user install, Vietnamese wizard,
#   desktop + Start-menu shortcuts, proper uninstaller)
```

The installer kills a running instance before copying files, so
upgrades are just "run the new setup".

## Project layout

```
src\
  App.xaml(.cs)          Entry point: single-instance mutex, tray icon and
                         menu, global exception trap → crash.log
  MainWindow.xaml(.cs)   Host window: WebView2 setup, the bridge script,
                         hotkey hook engine, capture/record flows,
                         right-click menu, chrome show/hide
  Hotkeys.cs             Binding model + hotkeys.json persistence
  HotkeyDialog.xaml(.cs) Assign-any-key dialog (PreviewKeyDown capture)
  RegionWindow.xaml(.cs) Lens selector overlay (physical-pixel pinned)
  WindowPlacement.cs     Physical-pixel pinning helpers — ported from
                         WinLens (MIT, Marco Beltrame; see Credits)
  Capture.cs             GDI screen/region capture
  Recorder.cs            ffmpeg gdigrab recording lifecycle
  Native.cs              P/Invoke: hooks, hotkeys, DWM dark caption,
                         synthetic keystrokes, window metrics
  CrashLog.cs            %LocalAppData%\MuseDesktop\crash.log writer
  Autostart.cs           HKCU Run-key toggle
installer.nsi            NSIS per-user installer script
pack\HUONG_DAN.txt       End-user guide (Vietnamese), shipped in the app folder
lab\                     Bridge test harness: replica pages + CDP-driven
                         tests run against Chrome for Testing
clone_app.py             Re-skins this codebase into a sibling app for
                         another chat site (see below)
```

## Sibling apps

The same design has been cloned for other chat sites with
`clone_app.py`, which copies the tree and rewrites the identity —
namespace, data folder, single-instance mutex/event names, installer,
window title, home URL — and adds a plain-Chrome `UserAgent` override
(some sign-in flows refuse embedded-webview agents):

```
python3 clone_app.py <target-dir> <Namespace> <DisplayName> <HomeUrl> <SetupExeName>
```

This produced **ChatGPT Desktop** (`https://chatgpt.com`) and
**Gemini Desktop** (`https://gemini.google.com`), both v1.0.0, which run
side by side with Muse Desktop because every per-app identifier above
differs. Note that identical default hotkeys in two *running* siblings
will both fire — rebind per app if you keep several resident.

## Testing

There is no unit-test suite; the bridge is verified behaviourally in
`lab\` with Chrome for Testing driven over CDP (real input events, a
mock `chrome.webview`):

- `test-ctxmenu.js` — a real right-click posts `muse-contextmenu`, the
  page's own contextmenu listener never runs unprevented, composer focus
  still works (3/3).
- `test-spell.js` — after the bridge runs, pre-existing and
  SPA-created editables all report `spellcheck === false` (3/3). This
  test caught the `documentElement` observer bug described above before
  it shipped.
- `test-bridge*.js` — legacy suites from the removed attach-on-send
  design, kept for reference.

The WPF host itself cannot execute in the build environment (no Windows
runtime), so host-side changes are verified by compilation, by code
inspection against the failing behaviour, and by on-device test rounds.

## Known limitations

- **Snapshot, not a stream.** The app routes discrete captures into the
  chat; it is not a continuous screen feed, and it cannot replicate
  Google Lens visual search or Drive indexing.
- **Login lives in WebView2.** Sign-in is the site's own flow inside an
  embedded Chromium; a provider that hard-blocks embedded browsers
  (Google is the strictest) can still refuse despite the Chrome UA.
- **MP4 paste depends on the site.** Whether a pasted video file becomes
  an attachment is the web app's decision, not the host's.
- **Recording has no audio** (gdigrab video only) and records one
  monitor at a time.
- **Global bare-key bindings are global.** See the warning above.
- ffmpeg makes the installer ~73 MB; almost all of it is that binary.

## Troubleshooting

- **A hotkey does nothing** — check the status bar line (it lists the
  live bindings), make sure no sibling app with the same binding is the
  one actually firing, and check `crash.log` for a hook-install failure.
- **Captures paste nowhere** — the composer must exist on the page (be
  in a chat view); the bridge focuses the lowest visible editable.
- **WebView2 errors on first run** — the installer runs the bundled
  bootstrapper when the runtime is missing; a working internet
  connection is required for that one step.
- **Something crashed** — the app traps unhandled exceptions, keeps
  running, and writes details to `crash.log`.

## Credits

- **WinLens** by Marco Beltrame (MIT) — the lens selection mechanics and
  the physical-pixel window pinning in `WindowPlacement.cs` /
  `RegionWindow` are ported from its source:
  <https://github.com/marco-beltrame/WinLens>
- **ffmpeg** (LGPL/GPL builds via ffmpeg-static) — screen recording.
- **Microsoft WebView2** — the embedded browser runtime.

## Version history (condensed)

- **1.0.0** — WebView2 shell, tray, Alt+Space, capture-both-screens.
  The 7z-SFX installer failed on the target machine and was replaced by
  a real NSIS installer the same day.
- **1.1.x** — Google-style source picker + attach-on-send via a global
  keyboard hook; three fix rounds.
- **1.2.x** — attach-on-send rebuilt on an in-page bridge; a JSON
  double-encoding bug in the host silently dropped every page message.
- **1.3.x** — bridge event-trust fixes, page-wide attachment counting,
  verified send release.
- **1.4.x** — WinLens lens mechanics ported (physical pixels, DPI-v2);
  lens becomes a first-class capture path.
- **1.5.x** — attach-on-send **removed by design** at the user's order;
  three capture buttons, assignable hotkeys, collapsible chrome.
- **1.6.0** — screen recording via bundled ffmpeg.
- **1.6.1** — chrome hidden leaves zero leftover UI (host-drawn
  right-click menu), dark title bar, any-key bindings unlocked.
- **1.6.2** — right-click menu rebuilt: in-page `contextmenu` hook +
  host-drawn WPF menu (the WebView2 menu-items route never appeared on
  the target machine).
- **1.6.3** — spell-check disabled in two layers.
- **1.6.4** — hotkey engine replaced (`RegisterHotKey` →
  `WH_KEYBOARD_LL`), toolbar restyled (rounded pill buttons), hint text
  removed. First version confirmed end-to-end by the user.

## License

Muse Desktop is free software, licensed under the **GNU General Public
License v3.0** — see [LICENSE](LICENSE) for the full text. You may use,
study, modify, and redistribute it; if you distribute a modified
version, that version must be licensed under the GPL as well, with its
corresponding source available. (The copyright holder chose GPL over a
permissive license deliberately: derivatives stay open.)

Portions derived from WinLens (`WindowPlacement.cs` and the lens
mechanics in `RegionWindow`) remain under the **MIT License** of the
original project, Copyright (c) Marco Beltrame — see *Credits*. ffmpeg
and the WebView2 runtime are third-party components under their own
licenses and are not part of this repository.
