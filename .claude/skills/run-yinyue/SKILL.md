---
name: run-yinyue
description: Build, launch, observe, and cleanly shut down the Yinyue app. Use whenever asked to run, start, restart, debug-launch, or screenshot Yinyue, or to confirm a change works in the real app rather than only in tests. Also covers diagnosing a launch that appears to do nothing.
---

# Running Yinyue

Yinyue is a **system-tray daemon with no main window**. Launching it successfully looks
identical to launching it unsuccessfully from the terminal: no output, no window, process
stays alive. Follow this procedure so you can tell the difference and so you never leave
orphaned processes behind.

## Build first, always

```bash
dotnet build win/Yinyue.csproj -v minimal --nologo
```

Takes ~3 seconds and is expected to be **completely clean — zero warnings**. If you see a
warning, you introduced it. (`WFAC010` is suppressed deliberately in the csproj; the
comment there explains why.)

## Before launching: clear stale instances

`App.OnStartup` takes a named mutex (`Yinyue-MusicDaemon-5A3E9C12-...`). A second instance
shows a **modal MessageBox** and exits. In a non-interactive session that dialog blocks
with nothing on screen to dismiss, which looks exactly like a hang. Always check first:

```bash
tasklist //FI "IMAGENAME eq Yinyue.exe"
```

Note the `//FI` — the Bash tool runs Git Bash, which mangles a single leading slash into a
path. Use PowerShell (`Get-Process Yinyue -ErrorAction SilentlyContinue`) if you prefer.

Kill leftovers before launching:

```bash
taskkill //F //IM Yinyue.exe
```

## Launching

Always bound the run. The process never exits on its own, so an unbounded launch hangs the
tool call until timeout and leaves the process alive.

```bash
cd win && (timeout 15 ./bin/Debug/net8.0-windows10.0.19041.0/Yinyue.exe; echo "EXIT=$?") 2>&1 | head -30
```

Reading the result:

- **`EXIT=124`** — `timeout` killed it, meaning it survived the full window. This is
  success: startup completed, the tray icon registered, no unhandled exception.
- **`EXIT=0` immediately** — it hit the single-instance path and exited. Kill the other
  instance and retry.
- **Non-zero, fast** — a real startup crash. WPF sends unhandled-exception detail to the
  debugger, not stdout, so the terminal may be empty; see *Diagnosing a silent crash*.

Afterwards, confirm nothing survived:

```bash
tasklist //FI "IMAGENAME eq Yinyue.exe"
```

## What you cannot verify from the terminal

Be honest about this rather than claiming a feature works. Headlessly you can confirm the
process starts, stays up, and exits cleanly. You **cannot** confirm the overlay renders at
the right position, the tray menu works, global hotkeys fire, or media keys route correctly
— all of those need a real interactive desktop session. For those, build, state plainly
that verification needs a manual check, and say exactly what to look for.

## Diagnosing a silent crash

**Check the log first.** `App` handles `DispatcherUnhandledException` and appends to
`%APPDATA%\Yinyue\yinyue.log`, so most crashes leave a trace:

```bash
cat "$APPDATA/Yinyue/yinyue.log"
```

An absent or empty log after a clean 12-second run means no unhandled exception occurred.

If the log is silent but something is still wrong, `Debug.WriteLine` output (used
throughout `Services/`) goes to the debugger, not the console. Two further options:

1. **Event Log** — unhandled .NET exceptions land there:
   ```bash
   powershell -c "Get-EventLog -LogName Application -Source '.NET Runtime' -Newest 3 | Format-List TimeGenerated,Message"
   ```
2. **Temporary console** — add `<OutputType>Exe</OutputType>` to the csproj to get a console
   window alongside the WPF app. Revert before committing.

## Resetting app state

Everything persistent lives in one folder. Clearing it gives a true first-run:

```bash
ls -la "$APPDATA/Yinyue"          # config.json, tracks.db, art/, yinyue.log
rm -rf "$APPDATA/Yinyue"
```

Deleting this discards the user's configured library folders, the indexed library, the
saved Jellyfin token, and the stable `DeviceId` — forcing a rescan, a re-login, and a new
server-side session. **Confirm before doing it**, and prefer deleting a single file
(`tracks.db` for an index-only reset) over the whole folder.

## Publishing a real build

```bash
dotnet publish win/Yinyue.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

Output lands in `win/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/`.
