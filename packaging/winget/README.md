# winget manifest

Staged [winget](https://learn.microsoft.com/windows/package-manager/) manifest for
`winget install ItayCohen.CodexWinBar`. Three files per the multi-file manifest format
(version / installer / locale), schema 1.6.0.

## Submitting a version

winget requires the manifest to point at a **published** release asset and match its SHA256 exactly.
So the flow is: cut the GitHub release first, then update + submit the manifest.

1. **Publish the release** (push a `v*` tag → the *Release (Windows)* workflow builds and uploads
   `CodexWinBar-win-Setup.exe`).

2. **Update `PackageVersion`** in all three files and the `InstallerUrl` (the `v<version>` in the path).

3. **Recompute `InstallerSha256`** from the *published* asset (a CI build differs byte-for-byte from a
   local one, so the hash in the file is only a placeholder until you do this):

   ```powershell
   $u = "https://github.com/ItayCohen-Prog/CodexWinBar/releases/download/v1.0.0/CodexWinBar-win-Setup.exe"
   Invoke-WebRequest $u -OutFile setup.exe
   (Get-FileHash setup.exe -Algorithm SHA256).Hash
   ```

4. **Validate & submit.** Easiest is [`wingetcreate`](https://github.com/microsoft/winget-create):

   ```powershell
   winget install Microsoft.WingetCreate
   wingetcreate update ItayCohen.CodexWinBar --version 1.0.0 --urls <installer-url> --submit
   ```

   Or manually: `winget validate .` here, then open a PR to
   [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) placing the files under
   `manifests/i/ItayCohen/CodexWinBar/<version>/`.

> Note: CodexWinBar auto-updates itself via Velopack, so winget mainly serves as an install/discovery
> path — users stay current without `winget upgrade`.
