# Slasshy Url Interceptor (Windows Tray App)

A lightweight Windows tray app that captures redirect URLs when desktop apps open a browser (for example, `Sign in with Google`).

## Core behavior

- Runs in the background from the system tray.
- Captures URLs passed in process launch commands (browser and launcher/helper processes).
- Records parent process info (which app opened the browser).
- Instantly copies every intercepted URL to clipboard.
- Lets you turn interception ON/OFF from tray right-click.
- Lets you exclude specific parent applications from interception.
- Optional redirect-chain resolution (off by default for lower overhead).
- Supports full interception mode by setting this app as default `HTTP/HTTPS` handler, then forwarding to your chosen real browser.

## Tray menu

Right-click the tray icon and use:

- `Turn Interception On/Off`
- `Excluded Apps...`
- `Open Intercepted Links In Browser` (ON/OFF)
- `Forward Browser: ...` (select browser executable used after interception)
- `Open Default Apps Settings` (set this app as default HTTP/HTTPS app for full coverage)
- `Resolve Redirect Chain`
- `Launch At Startup`
- `Open Logs Folder`
- `Open App Data Folder`
- `Exit`

## Data location

All app data is stored in:

```text
%LocalAppData%\SlasshyUrlInterceptor
```

Files:

- `config.json` (settings)
- `app.log` (internal app errors/status)
- `logs\intercepts-YYYYMMDD.jsonl` (captured URLs)

## Build (dev)

```powershell
dotnet build .\RedirectUrlInterceptor\RedirectUrlInterceptor.csproj
```

## Publish EXE

```powershell
dotnet publish .\RedirectUrlInterceptor\RedirectUrlInterceptor.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```

Output EXE:

```text
RedirectUrlInterceptor\bin\Release\net8.0-windows\win-x64\publish\SlasshyUrlInterceptor.exe
```

## GitHub Actions CI/CD

Workflow file: `.github/workflows/build-release.yml`

- On push/PR to `main`: restores, builds, and publishes the app on `windows-latest`.
- On tag push matching `v*` (example: `v1.0.0`): publishes build artifacts and creates a GitHub Release.
- Release assets:
  - `SlasshyUrlInterceptor.exe`
  - `SlasshyUrlInterceptor-win-x64.zip`
- Release notes are generated from commit history between the current tag and previous tag.

Create a release from your machine:

```powershell
git tag v1.0.0
git push origin main --tags
```

## Output format

Each line in `intercepts-YYYYMMDD.jsonl` is one JSON record:

```json
{"TimestampUtc":"2026-02-21T18:03:10.031Z","BrowserProcess":"msedge.exe","BrowserPid":20840,"ParentProcess":"myapp.exe","ParentPid":10324,"Url":"https://accounts.google.com/o/oauth2/v2/auth?...","RedirectTrace":null}
```

## Limitations

- Best-effort capture in monitor mode.
- Full guaranteed capture requires setting this app as the default handler for `HTTP` and `HTTPS` in Windows settings.
- Does not capture in-page JavaScript router redirects inside already opened tabs.

## Full interception setup (required for apps like FxSound)

1. Run the tray app once as normal.
2. Right-click tray icon -> `Forward Browser: ...` and select your real browser EXE (`chrome.exe`, `msedge.exe`, etc.).
   If you only want clipboard capture and no browser open, turn OFF `Open Intercepted Links In Browser`.
3. Right-click tray icon -> `Open Default Apps Settings`.
4. Set both `HTTP` and `HTTPS` protocol defaults to `Slasshy Url Interceptor`.

## Security note

Captured OAuth/auth URLs may contain sensitive query parameters (codes/tokens). Protect logs appropriately.
