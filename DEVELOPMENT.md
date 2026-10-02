# Developing Clearspace

<!-- NEW (user readme): the build, release and project notes that used to be in README.md. The README is
     now for the person installing and using Clearspace. -->

This page is for building Clearspace from its source code and publishing releases. To install and use
Clearspace, see the [README](README.md).

## What you need

- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- [Inno Setup](https://jrsoftware.org/isdl.php) (`winget install -e --id JRSoftware.InnoSetup`), only for
  building the installer. The dark look needs Inno Setup 6.6 or newer, and 6.7 or newer for Clearspace's
  exact background color; older versions still build, with a light installer.

## Development build

Run [Build Clearspace.cmd](Build%20Clearspace.cmd) to publish a local executable to `dist\` and create a
desktop shortcut. [Run Clearspace.cmd](Run%20Clearspace.cmd) is the rebuild-and-launch option for
development. [Clean.cmd](Clean.cmd) removes the build output.

## Tests

```text
dotnet test Clearspace.Tests/Clearspace.Tests.csproj -c Release
```

See [Clearspace.Tests/README.md](Clearspace.Tests/README.md) for what the tests cover.

## Building the installer

Run [Build Installer.cmd](Build%20Installer.cmd). It publishes a self-contained 64-bit build, then creates:

```text
release\ClearspaceSetup.exe
```

.NET, WPF and SQLite are built into `Clearspace.exe` (and into the index helper); everything else
Clearspace uses is part of Windows 10 and 11, so the setup file works offline and downloads nothing. The
installer script refuses to build if the published app is not the self-contained one.

The same setup file installs, updates, repairs and removes Clearspace. Run it on a PC that already has
Clearspace to try the update page.

## Publishing an update

The version number comes from the [VERSION](VERSION) file; [VERSIONING.md](VERSIONING.md) says what each
number means and when to raise it.

1. Raise the number in [VERSION](VERSION) by the rules in [VERSIONING.md](VERSIONING.md) (for example `1.3.0`).
2. Update *What's new* in the [README](README.md).
3. Commit, then run `Build Installer.cmd`.
4. Create a GitHub release tagged `v1.3.0` — the tag must match `VERSION` — and attach
   `release\ClearspaceSetup.exe` under that exact file name. The release description is what the update
   dialog shows.

Installed copies find the release through the update check described in the README. Copies of Clearspace
older than 1.2.0 have no update check, so they need a newer installer run once by hand.

## Project layout

```text
Clearspace/              WPF application source
Clearspace.IndexHelper/  Optional Windows service for instant indexing and fast catch-up
Clearspace.Tests/        Regression tests (run with dotnet test)
docs/                    Design notes and the pictures used in the README
installer/               Inno Setup installer definition
output/                  Saved test reports and coursework packages
dist/                    Local published build (generated)
release/                 Installer output (generated)
README.md                For the person installing and using Clearspace
DEVELOPMENT.md           This page
VERSION                  The version number, read by both projects and the installer
VERSIONING.md            What each part of the version number means and when to raise it
```

## Design notes

- [Clearspace/ARCHITECTURE.md](Clearspace/ARCHITECTURE.md): how the app is put together.
- [Clearspace/INDEX-DESIGN.md](Clearspace/INDEX-DESIGN.md) and
  [docs/Search-and-Indexing-Plan.md](docs/Search-and-Indexing-Plan.md): the file index and search.
- [Database notes](docs/CS499-Artifact-Three-Databases.md): how tags are stored, with migration, backup
  and recovery details.
- [File-locking notes](docs/CS499-File-Locking.md): the lock format, tests and recovery limitations.
- [Algorithm notes](docs/CS499-Algorithms-Disk-Usage.md): the disk usage map's design decisions,
  complexity, tests and limitations.
