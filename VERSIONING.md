# Clearspace version numbers

<!-- NEW (versioning): what each number means and how to raise it. -->

Clearspace has one version number, in the [VERSION](VERSION) file at the repository root. Building stamps
it into `Clearspace.exe`, `ClearspaceIndexHelper.exe` and the installer; the About window shows it; the
update check compares it with the newest GitHub release tag.

It is always three plain numbers: **major.minor.patch**, for example `1.3.0`.

## What each number means

| Number | Raise it when | Examples |
|---|---|---|
| **Major** (`X.0.0`) | Updating costs the user something. | A feature is removed, or reworked so that what people did before no longer works. Tags, locks or settings are dropped or changed by the update. Clearspace needs a newer Windows than before. |
| **Minor** (`1.X.0`) | There is something new to see or use. | Tabs, themes, a new view, a new setting or command, the update check. A data change the update carries out by itself without losing anything. |
| **Patch** (`1.2.X`) | Nothing is new, only better. | Bug fixes, speed and memory work, wording, installer fixes. |

Ask the questions from the top: does updating cost the user something? Then major. Otherwise, is there
something new? Then minor. Otherwise patch.

## Rules

1. **One step per release, and the biggest change wins.** Three fixes and one new feature make a minor
   release. Ten new features still raise minor by one.
2. **Lower numbers go back to zero.** `1.2.3` becomes `1.3.0`, not `1.3.3`. `1.9.4` becomes `2.0.0`.
3. **Numbers are not decimals.** After `1.9.0` comes `1.10.0`.
4. **Always three plain numbers.** No `-beta`, no `v` in the file, no fourth number: the update check and
   the installer only understand `1.3.0`. A test build is published as a GitHub *pre-release*, which the
   update check ignores.
5. **A published number is frozen.** Never reuse one and never go down. A release that turns out broken is
   fixed by the next patch, not by uploading a new file under the same number: the download check and
   *Skip this version* both go by the number.
6. **`VERSION` is raised once, for the release.** It goes up in the commit that is released, and that
   commit gets the tag `v<VERSION>`. Tag and file always match. Builds made between releases show the
   number of the last release.
7. **Only what ships counts.** A change to the README, `docs/`, tests or build scripts alone does not need
   a new version or a release.

## Examples

Starting from a released `1.2.0`:

| The next release contains | It is |
|---|---|
| Tabs, and bug fixes for 1.2.0 | `1.3.0`. The fixes ship with the feature; they do not add a step of their own. |
| Bug fixes only, tabs are not ready | `1.2.1`. Tabs become `1.3.0` when they ship. |
| Bug fixes only, after `1.3.0` is out | `1.3.1` |
| Two new features and a removed one | `2.0.0` |

A patch number above zero therefore always means: a release made after that minor release, with fixes only.

## Numbers that are not the version

Three parts of Clearspace count their own format. They go up when that format changes and never to keep
pace with the version above.

| Number | Where | Now | A change means |
|---|---|---|---|
| Index file format | `FormatVersion` in `Clearspace/Services/FileIndexStore.cs` | 3 | The saved index is rebuilt once in the background. Nothing is lost, so this alone is a patch or minor release. Say so in the release notes: the first start is slower. |
| Tag database schema | `PRAGMA user_version` in `Clearspace/Services/TagDatabase.cs` | 5 | The update moves the database forward by itself, so this alone is a minor release. An older Clearspace refuses a newer database, so the release notes must say that going back to the earlier version is not possible. |
| Index helper protocol | `JournalProtocol.Version` in `Clearspace/Journal/JournalCatchUp.cs` | 1 | Clearspace and the helper are installed together, so this needs no version step of its own. |

## Releasing

1. Decide the new number with the table above and write it in [VERSION](VERSION).
2. Update *What's new* in the [README](README.md).
3. Commit, then run `Build Installer.cmd`.
4. Create a GitHub release tagged `v<VERSION>` on that commit and attach `release\ClearspaceSetup.exe`
   under that exact name. The release description is what the update dialog shows: list what is new,
   and anything from the table above (a rebuild, no way back).

## History

The releases tagged `v1.0.0` and `v1.1.0` were both built with the number 1.0.0 inside. `v1.2.0` is the
first one whose tag and number match, and the first with the update check.
