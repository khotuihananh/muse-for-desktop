# Contributing to Muse Desktop

Thanks for your interest. This is a small, single-purpose Windows app:
a WebView2 shell around a chat web app, plus screen capture / recording
plumbing. Contributions that keep it small and single-purpose are the
most welcome — bug reports from machines other than the author's are
especially valuable, because most historical bugs were environment
specific (DPI mixes, dead API layers, site DOM differences).

## Getting set up

Follow **Building from source** in the README. Two payloads are not in
the repo (ffmpeg, the WebView2 bootstrapper); run the helper to fetch
them into the right places:

```powershell
powershell -ExecutionPolicy Bypass -File tools\fetch-deps.ps1
```

Then publish + stage + `makensis installer.nsi` as the README describes.

## Where things live

- `src/MainWindow.xaml.cs` — host window and the in-page **bridge
  script** (a string constant near the top of the capture section).
- `src/Native.cs` — all P/Invoke. New Win32 calls go here, not inline.
- `lab/` — behavioural tests for the bridge (Chrome for Testing + CDP).
  If you change the bridge, run the relevant `test-*.js` and add a case
  for the behaviour you changed.

## House rules (learned the hard way)

1. **No silent failures.** If a mechanism can fail, it logs to
   `crash.log` and, where the user is affected, says so on the status
   bar. A `try { } catch { }` that swallows a user-visible feature is a
   bug.
2. **A mechanism that dies silently gets replaced, not patched.** This
   project burned several releases re-patching `RegisterHotKey` and the
   WebView2 context-menu API before replacing both (keyboard hook;
   in-page hook + host-drawn menu). If your fix is the third patch on
   the same dead layer, step back and replace the layer.
3. **The bridge stays minimal.** It focuses the composer, reports
   right-clicks, and disables spell-check. It does not read message
   content and does not intercept sending — that design existed and was
   removed on purpose.
4. **Verify rendered behaviour, not just compilation.** The host can't
   run in CI here; bridge changes are verified in `lab/`, and host
   changes should state clearly what was and wasn't exercised.
5. **Physical pixels are the only pixels.** Capture and lens code works
   in physical screen coordinates end to end (PerMonitorV2). Don't
   introduce DIP conversions outside `WindowPlacement`.

## Pull requests

- One change per PR, with a short "what / why / how verified" note.
- If you touched the bridge, include the lab result.
- Version bumps and installer changes ride along in the same PR when
  the change is user-visible.

## Good first contributions

- Test the installer and the app on a machine with a single monitor,
  with 125%/150% display scaling, or on Windows 10, and file what
  breaks.
- A sibling skin for another chat site via `clone_app.py`.
- Translations of the installer / in-app strings (currently
  Vietnamese-only).
