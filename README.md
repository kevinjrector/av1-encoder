# AV1 Encoder

Batch AV1 video encoder for Windows using NVIDIA NVENC.

## Requirements

- Windows 11, x64
- NVIDIA RTX 40 series or newer GPU with a current driver. Intel and AMD GPUs are not supported.
- FFmpeg. If it is missing, the app offers to install it with winget.

## Install

Download `AV1-Encoder-Setup-<version>.exe` from the Releases page and run it.
It installs for the current user, so no admin prompt appears, and adds a Start menu entry.
Windows SmartScreen may warn about an unrecognized app because the installer is not code-signed. Choose More info, then Run anyway.

Uninstall from Settings > Apps. Settings, logs, and FFmpeg are kept.

## Usage

1. Pick a source folder and a destination folder.
2. Pick 1, 2, or 3 workers.
3. Pick a quality: Low (smallest files), Default, or High (closest to the original).
4. Press Start.

Files are encoded smallest first. The window shows files completed, space saved, and time left.
Originals are never changed. Press Stop or close the window at any time, and press Start later to continue.

## Logs

Logs are in `%LocalAppData%\Av1Encoder\logs` and rotate by size. The Logs menu opens them.

- `app.log`: readable log of runs, files, and errors.
- `encodes.jsonl`: one JSON record per file and per run, for metrics.

Total space saved, in PowerShell:

```powershell
$records = Get-Content "$env:LOCALAPPDATA\Av1Encoder\logs\encodes*.jsonl" | ConvertFrom-Json
'{0:N1} GB' -f (($records | Where-Object result -eq 'done' | Measure-Object savedBytes -Sum).Sum / 1GB)
```

Failures:

```powershell
$records | Where-Object result -eq 'failed' | Select-Object ts, file, reason
```

## Build

Requires the .NET 10 SDK and Inno Setup 7 (`winget install JRSoftware.InnoSetup.7`).

```powershell
.\build.ps1
```

The installer is written to `artifacts\AV1-Encoder-Setup-<version>.exe`. The version comes from `<Version>` in `src\Av1Encoder\Av1Encoder.csproj`.

## License

MIT. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
