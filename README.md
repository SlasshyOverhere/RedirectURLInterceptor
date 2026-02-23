# Slasshy Url Interceptor

Lightweight Windows tray app that intercepts redirect links from desktop apps, copies them to clipboard instantly, and can forward them to your browser.

![Slasshy Url Interceptor use-case demo](docs/media/usecase.gif)

## Why this exists

Many apps trigger browser redirects for sign-in, preset downloads, OAuth flows, and deep links. Slasshy Url Interceptor gives you control over those links:

- captures the URL before it disappears into the browser.
- copies it to clipboard immediately.
- optionally forwards it to your preferred browser.
- logs source app + process for debugging and automation.

## Common use cases

- Capture OAuth links (`Sign in with Google`, Microsoft login, etc.).
- Capture one-click resource links from tools like audio/util apps.
- Debug which app launched a link and with what URL.
- Build automations that depend on intercepted URL data.

## Features

- Always-on tray app, built for low overhead.
- `HTTP/HTTPS` protocol interception mode for full coverage.
- On/off switch from tray menu.
- Exclusion list for apps you do not want to intercept.
- Optional redirect-chain resolution.
- Optional browser forwarding after interception.
- Auto update from GitHub Releases.
- JSONL logging for machine-readable history.

## Quick start (recommended)

1. Download latest `SlasshyUrlInterceptor.exe` from Releases.
2. Run the EXE once (tray icon appears).
3. Right-click tray icon -> `Open Default Apps Settings`.
4. Set both `HTTP` and `HTTPS` defaults to `Slasshy Url Interceptor`.
5. In tray, choose `Forward Browser: ...` and select your real browser executable (`chrome.exe`, `msedge.exe`, etc.).
6. Keep `Open Intercepted Links In Browser` ON if you want normal browsing behavior after capture.

## Tray menu options

- `Turn Interception On/Off`
- `Excluded Apps...`
- `Open Intercepted Links In Browser`
- `Forward Browser: ...`
- `Open Default Apps Settings`
- `Enable Auto Update`
- `Check For Updates...`
- `Reinstall From Latest Release...`
- `Resolve Redirect Chain`
- `Launch At Startup`
- `Open Logs Folder`
- `Open App Data Folder`
- `Exit`

## Data storage

Path:

```text
%LocalAppData%\SlasshyUrlInterceptor
```

Files:

- `config.json` for settings.
- `app.log` for runtime/status logs.
- `logs\intercepts-YYYYMMDD.jsonl` for captured URL records.
- `updates\` temporary files used by auto-update/install.

Example JSONL record:

```json
{"TimestampUtc":"2026-02-21T18:03:10.031Z","BrowserProcess":"msedge.exe","BrowserPid":20840,"ParentProcess":"myapp.exe","ParentPid":10324,"Url":"https://accounts.google.com/o/oauth2/v2/auth?...","RedirectTrace":null}
```

## Build from source

```powershell
dotnet build .\RedirectUrlInterceptor\RedirectUrlInterceptor.csproj -c Release
```

## Publish EXE

```powershell
dotnet publish .\RedirectUrlInterceptor\RedirectUrlInterceptor.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```

Published binary:

```text
RedirectUrlInterceptor\bin\Release\net8.0-windows\win-x64\publish\SlasshyUrlInterceptor.exe
```

## CI/CD and releases

Workflow: `.github/workflows/build-release.yml`

- Push/PR to `main`: build validation on `windows-latest`.
- Push tag `v*`: build + publish release assets + release notes.

Release flow:

```powershell
git tag v0.0.2
git push origin main --tags
```

## Limits and notes

- Full guaranteed capture needs protocol assignment (`HTTP` + `HTTPS`).
- In-page JavaScript redirects inside an already open browser tab are outside process-launch interception.
- Captured URLs may contain sensitive OAuth parameters. Protect logs accordingly.
