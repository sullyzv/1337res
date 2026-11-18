# 1337res

Switch your Windows resolution and refresh rate from a saved profile or a keyboard shortcut.

**[Download for Windows x64](https://github.com/sullyzv/1337res/releases/latest/download/1337res-win-x64.exe)** · [Project website](https://sullyzv.github.io/1337res/) · [Release notes and checksums](https://github.com/sullyzv/1337res/releases)

## Get started

1. Download and open `1337res-win-x64.exe` on Windows 11 or a supported Windows 10 release, x64. The executable includes the .NET runtime and runs without an installer.
2. Select the monitor you want to change. Pick a listed mode or enter a custom width, height, and refresh rate.
3. Apply the mode and select **Keep mode** within 15 seconds. Choose **Revert** or let the timer expire to restore the previous mode. Kept changes last for the Windows session.
4. Save the mode as a named profile and assign a hotkey if you want one. For example, assign `Ctrl` + `Alt` + `1` to your desktop mode and `Ctrl` + `Alt` + `2` to a game mode.

## Profiles and tray controls

- A profile remembers its monitor, resolution, refresh rate, and optional hotkey. Use it from the app, the tray menu, or its shortcut while 1337res is running.
- **Close to tray** keeps hotkeys active when you close the window. **Start with Windows** opens 1337res in the tray when you sign in. If you move the executable, turn the startup option off and back on from its new location.
- Custom modes are checked against your graphics driver. If a mode is rejected, choose a listed mode or add the desired mode through your graphics driver’s tools first, if supported.
- Your profiles are saved locally in `%LOCALAPPDATA%\1337res\settings.json`.

## Build from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows and run these commands in PowerShell.

```powershell
git clone https://github.com/sullyzv/1337res.git
cd 1337res
powershell -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\publish.ps1
```

The program and its SHA-256 checksum are written to `artifacts\win-x64\`. To verify a download, compare the displayed hash with the accompanying `.sha256` file.

```powershell
Get-FileHash .\1337res-win-x64.exe -Algorithm SHA256
```

The desktop source lives in `src/1337res/` and the project website lives in `docs/`. GitHub Actions builds changes to `main`. Maintainers can publish a release by pushing a version tag such as `v1.0.0` or running **Windows build and release** on `main` with a version such as `1.0.0`.

## Credits

Created by **harm**. Discord username `harm`. [Join the Discord](https://discord.gg/FGk4fnYeCP)
