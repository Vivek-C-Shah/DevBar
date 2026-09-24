# Code signing policy

SignPath Foundation's conditions for free open-source code signing require this
page to exist and to say these specific things. It is also the honest answer to
"who built the thing I am about to run as my user".

## Status

**DevBar is not signed yet.** The installer you download from Releases is
unsigned, so Windows SmartScreen will warn you the first time you run it:
**More info → Run anyway**. You can also build it yourself with
`.\scripts\build-installer.ps1`, or check the SHA256 published on the release
against your download.

Everything below describes the arrangement once signing is in place.

## Who signs, and who the publisher is

The certificate is issued to **SignPath Foundation**, not to an individual. That
means Windows will name **SignPath Foundation** as the publisher of DevBar, and
the signature says the binary came from this project's source through a build
nobody hand-carried. It is not a statement that Microsoft or SignPath endorse
the software.

Free code signing provided by [SignPath.io](https://signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

## Team roles

DevBar is maintained by **[Vivek C Shah](https://github.com/Vivek-C-Shah)**, who
owns the source repository, reviews and merges every change, and approves every
signing request. There is no separate signing team; if that changes, this page
changes with it.

All maintainer accounts with access to the repository or to signing use
multi-factor authentication.

## How a signed build is produced

1. A release is cut by pushing a `v*` tag.
2. GitHub Actions (`.github/workflows/release.yml`) builds the installer on a
   clean runner from that tag, using the same `scripts/build-installer.ps1` a
   maintainer would run locally. The workflow refuses to build if the tag, the
   version in `DevBar.csproj` and the version in `DevBar.iss` disagree.
3. The build is submitted for signing, and a maintainer approves that signing
   request before anything is published.
4. The signed installer and its SHA256 are attached to the GitHub release.

Nothing is signed that was built anywhere other than that workflow, and nothing
is signed from source that is not in this repository.

## Privacy

DevBar has no accounts, no telemetry and no analytics. With no keys configured,
nothing leaves your machine.

Jarvis, the voice module, is the one part that can talk to the internet, and
only when you configure it with your own API keys for the providers you choose.
Speech recognition for the wake word runs on-device. The microphone stays shut
until you press the hotkey, or until you explicitly turn the wake word on. Full
detail is in [PRIVACY.md](../PRIVACY.md).

## Reporting a problem with a signed build

Open an issue at
[github.com/Vivek-C-Shah/DevBar/issues](https://github.com/Vivek-C-Shah/DevBar/issues).
If it is a security issue, say so in the title and leave the detail out of the
public issue; a maintainer will follow up.
