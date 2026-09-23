# winget manifests

Ready to submit; they are not in winget until someone opens the PR.

```powershell
# 1. Publish the GitHub release first - winget validation downloads the
#    InstallerUrl below and checks it against InstallerSha256.
# 2. Sanity-check locally:
winget install --manifest .\packaging\winget\1.1.0

# 3. Submit (wingetcreate does the fork + PR for you):
winget install Microsoft.WingetCreate
wingetcreate submit .\packaging\winget\1.1.0
```

Or copy the three files into a fork of [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs)
at `manifests/v/VivekCShah/DevBar/1.1.0/` and open the PR by hand.

For the next version, `wingetcreate update VivekCShah.DevBar -u <new installer url> -v <version>`
recomputes the hash and opens the PR in one command.

**The hash must match the published asset.** If you rebuild the installer for any
reason, re-run `Get-FileHash dist\DevBar-Setup-<version>.exe -Algorithm SHA256`
and update `InstallerSha256`.
