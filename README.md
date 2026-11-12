# Gaming Hub for Windows

Gaming Hub gives your Windows 11 x64 gaming PC a fullscreen, controller-first interface. Pick a game from the artwork carousel on Home, browse your full Library, or change settings from the couch with a DualSense.

**Take your session to the TV.** Choose your TV as the preferred display and enable **Triple-press PS to extend the desktop** in Settings → Controller. When the TV is connected but Windows is using only your monitor, press PS three times. Gaming Hub extends the Windows desktop and moves the launcher onto the TV.

**Keep the controller in your hands.** Pin your own games to Home, search and filter your Library, and choose a launch display per game. During play, open the PS Quick Menu for game controls, display selection, controller batteries, and volume. Press Circle to return to the game. Optional audio switching can follow the hub to the TV.

**[Download the Windows installer](https://github.com/issazhd/gaming-hub/releases/latest)**

1. Open the latest release and download the file ending in **-Setup.exe** under **Assets**.
2. Run the installer. It installs Gaming Hub for your Windows account and includes the .NET runtime.
3. Open Gaming Hub, choose a display and scan your game library. Press **F4** to see the controls.

To update, run the newer installer over your existing installation. Your library and settings are stored separately from the program files.

Each release includes a SHA-256 checksum. To verify the download in PowerShell, run `Get-FileHash .\GamingHub-1.0.0-Setup.exe -Algorithm SHA256` from the download folder and compare the result with SHA256SUMS.txt.

To check for new releases, open **Settings → About → Check for updates**.

## Build a fork

Fork this repository and clone your fork on Windows. Install the .NET 10 SDK, then run:

```powershell
dotnet test GamingHub.sln
dotnet run --project src/GamingHub.App
```

Debug builds use a separate GamingHub-Dev data folder. To package an installer, install Inno Setup 6 and run `build/publish.ps1 -Installer`. The update checker points to the official repository. Fork maintainers distributing their own builds should change the release URLs and installer identity first.