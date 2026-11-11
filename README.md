# Gaming Hub for Windows

Gaming Hub is a fullscreen game launcher for Windows 11 x64. Browse games with a DualSense controller or keyboard, choose a monitor or TV, and open the Quick Menu for sound, displays and game controls.

**[Download the Windows installer](https://github.com/issazhd/gaming-hub/releases/latest)**

1. Open the latest release and download the file ending in **-Setup.exe** under **Assets**.
2. Run the installer. It installs Gaming Hub for your Windows account and includes the .NET runtime.
3. Open Gaming Hub, choose a display and scan your game library. Press **F4** to see the controls.

To update, run the newer installer over your existing installation. Your library and settings are stored separately from the program files.

Each release includes a SHA-256 checksum. To verify the download in PowerShell, run `Get-FileHash .\GamingHub-1.0.0-Setup.exe -Algorithm SHA256` from the download folder and compare the result with SHA256SUMS.txt.

Gaming Hub 1.0.0 starts the public version numbering. If you have 1.3.7, install 1.0.0 over it once. For subsequent releases, open **Settings → About → Check for updates**.

## Build a fork

Fork this repository and clone your fork on Windows. Install the .NET 10 SDK, then run:

```powershell
dotnet test GamingHub.sln
dotnet run --project src/GamingHub.App
```

Debug builds use a separate GamingHub-Dev data folder. To package an installer, install Inno Setup 6 and run `build/publish.ps1 -Installer`. The update checker points to the official repository. Fork maintainers distributing their own builds should change the release URLs and installer identity first.