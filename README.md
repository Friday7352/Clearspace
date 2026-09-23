# Clearspace

Clearspace is a modern Windows file manager built to be a comfortable replacement for File Explorer: familiar file operations, a calmer interface, and better ways to organize and find work.

> **Work in progress:** Clearspace 1.0.0 is an early public release. There will be bugs and rough edges as it is used on more Windows setups and file collections. Please report issues you find; fixes and improvements will continue over time.

## What Clearspace adds

### Folder types that adapt to your work

Any folder can be given a type such as **General**, **Documents**, **Downloads**, **Photos**, **Music**, or **Videos**. The type is saved for that location and changes the view to suit the content:

- Photos and Videos open naturally in a visual grid.
- Music uses a music-focused list with artist, album, track, and length columns.
- Documents, Downloads, Desktop, and General folders keep an efficient details view.
- Typed folders receive a matching visual badge, so the sidebar and grids are easier to scan.

Select several folders, right-click, and set their type together when organizing a larger collection.

### Built-in music player

Set a directory to **Music** and Clearspace can play supported tracks directly in the app. It reads the same Windows music properties used by Explorer for metadata such as artist, album, track number, and duration, while keeping ordinary double-click behavior available for opening files in their default app.

### Tags and Favorites

Tags are lightweight labels you can create and apply to one or many selected files and folders. They make it possible to group work across unrelated locations without moving anything on disk.

- Create, apply, remove, and delete tags from the right-click menu.
- Search by a tag name, or use `tag:work` for an exact tag filter.
- Pin frequently used locations to Favorites in the sidebar.
- Organize pinned locations into your own collapsible, reorderable categories.

### Photo viewer and editing

The Photos folder type has an in-app image viewer. Open an image from its tile to browse the folder, zoom, and pan without leaving Clearspace. You can also rotate an image or crop a selected region directly in the viewer, saving in place when the format supports it or saving a copy when needed.

### Search designed to stay responsive

Clearspace treats search as a layered operation instead of making the interface wait for a complete disk walk:

1. Filtering the folder already on screen happens immediately from its in-memory snapshot.
2. Search then walks subfolders in the background and streams additional matches into the results rather than freezing the window.
3. When enabled, Clearspace also asks the existing **Windows Search index** for fast results from indexed locations—including supported text inside documents—while the background walk continues to cover locations Windows has not indexed.

The result is a faster first response, plus complete results rather than a choice between speed and coverage. **Search Everywhere** expands the search to ready local drives and Clearspace's saved tag/folder-type indexes. Network drives are intentionally not crawled as part of that wide search, because an unavailable share should not make search feel stalled.

Useful search filters:

```text
tag:work           items with the Work tag
type:photos        folders set to the Photos type
ext:png            PNG files
is:folder          folders only
is:image           image files only
```

Clearspace maintains a compact filename index and also uses the Windows-maintained index for file contents. Content results depend on Windows indexing the location and having an IFilter for that file type. If a source is unavailable or lacks coverage, Clearspace can fall back to its background crawl.

### Disk usage visualizer

Use **Disk usage** in the toolbar or right-click menu to explore indexed file
sizes. Folder tiles show their combined file sizes; click one to drill down,
or compare sizes and matching colored icons in the compact list. Use
your mouse's side buttons for Back/Forward through visited folders and drives.
The view also includes breadcrumbs, Up, drive selection, and Refresh.
It displays a snapshot of logical file lengths, so recent
changes and folders excluded by the index scan may be missing. Refresh reads
the latest available index rather than starting a new scan.

The analyzer opens inside Clearspace; **Back to files** restores the browser.
The whole drive is one nested treemap that fills the space beside the list, and
the view is a camera moving over it. Click a block to fly into it: the folder
grows out of the exact rectangle you clicked, its contents fade in as they
get room, and the rest of the drive stays in place, dimmed, around it. Scroll to
zoom smoothly at the pointer, drag to pan, and zoom back out to leave a folder;
the list, breadcrumbs, and history follow along. Back, Up, breadcrumbs and your
mouse's side buttons fly the same camera, so every transition is continuous.
Escape (or **Whole folder**) returns to the full current folder after zooming inside it.

Blocks take one hue per branch - everything inside a top-level folder shares its
color - and each block within it gets its own shade, so a folder reads as one
region without its contents fusing into a flat field. List rows use the same
colors. Hover a block for its size, share, file type, and what a click will do. Tile areas are
exact byte proportions within each folder. Deeper folders load in the background
as they grow on screen, and very large folders group their smallest items so
drawing stays fast at any zoom.

A folder tree you have explored is kept when you close the analyzer or switch to
another drive, so coming back is immediate and keeps the depth you had opened.
With **Build in the background** on (the default), Clearspace keeps reading the rest
of the drive while you look at part of it, so zooming in later finds it already laid
out. What is on screen is always read first.

With **Render everything** on, the whole drive is laid out once when the analyzer
opens - about a second for a million files - into a compact table of roughly
30 MB per million files, so every block at every depth is on screen immediately
and zooming never waits for anything to load. The layout is kept when you switch
drives, and refreshes and file changes (up to six levels below the folder you are
in) lay it out again in the background and swap it in when it is ready.
Folder sizes are aggregated once and kept warm, so opening the analyzer shows the
map immediately instead of calculating first. They are recomputed when you choose
**Refresh** or when a full index build finishes; while the view is open it also
updates itself quietly in the background. Changing drives, resizing, and first
load cross-fade from the picture already on screen rather than blanking.

**Map performance** (the gear beside Refresh) holds two options, both remembered
between runs. *GPU acceleration* draws the blocks through Direct3D; turning it off
uses the built-in multi-threaded CPU rasterizer, which is what runs anyway on a
machine with no usable Direct3D adapter. The panel names the adapter in use.
*Render everything (experimental)* draws every block at every depth, so zooming
reveals nothing that was not already on screen and nothing pops in. The drawing
itself is nearly free on a discrete card; the cost is that the whole folder must be
read and held in memory, so watch the F3 memory line in a very large folder.
*Low performance mode* draws only the folder you are in - everything inside it
stays a solid block until you open it - so a frame never walks a deep tree. That
is the setting to reach for on integrated graphics or in enormous folders.

Initial layout, folder-path navigation, and detail loading run in the background.
The map batches tiles through the GPU (with a CPU fallback), limits work per frame,
and stops its animation loop when idle. Press **F3** in the analyzer to see frame
timings on your own drive. Closing the analyzer cancels its remaining work.
Map redraws yield to mouse and window input. Distant groups stay compact when
their fine detail would exceed the frame budget; zooming closer reveals their
contents at full pixel resolution.

Zoom detail fades gradually, and delayed GPU frames retain their matching labels
and hit targets. Background detail loading is limited during gestures; the sidebar
waits until zooming settles before replacing its listing.

The sidebar uses single-line name/size rows. Hover for full names and percentages;
the info button holds index details. Selection reveals the permanent-delete controls.

To **delete permanently**, right-click a file/folder block or select items in
the list and use the delete button or **Shift+Delete**. The confirmation lists
the targets and explains that they will bypass the Recycle Bin. Afterward, the
map and totals update for items confirmed removed, including partial deletions.

See [algorithm implementation notes](docs/CS499-Algorithms-Disk-Usage.md) for
design decisions, complexity, tests, and limitations.

## Familiar file-manager foundations

- Browse local, mapped network, and known Windows folders.
- Back, forward, up, breadcrumb navigation, side-mouse navigation, and an address bar.
- Copy, cut, paste, rename, new folder, delete to the Recycle Bin, and properties.
- Explorer-style right-click menus and Windows copy/conflict dialogs.
- This PC and Network hubs with drive capacity indicators.
- Per-folder details-column order and width, saved between sessions.
- Grid zoom is remembered independently for each folder.
- Optional terminal launch in the folder currently being viewed.

## Install or build

### Installable release

Run [Build Installer.cmd](Build%20Installer.cmd). It publishes a self-contained 64-bit build, then creates:

```text
release\ClearspaceSetup.exe
```

The setup is a normal one-click Windows installer. It installs Clearspace for the current user, includes the required .NET runtime, creates a Start Menu entry, and offers an optional desktop shortcut. Building the next version with the same installer updates the existing installation in place.

Building the installer requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and [Inno Setup 6](https://jrsoftware.org/isdl.php). End users only need the generated setup file.

### Development build

Run [Build Clearspace.cmd](Build%20Clearspace.cmd) to publish a local executable to `dist\` and create a desktop shortcut. [Run Clearspace.cmd](Run%20Clearspace.cmd) is the rebuild-and-launch option for development.

## Project layout

```text
Clearspace/      WPF application source
installer/       Inno Setup installer definition
dist/            Local published build (generated)
release/         Installer output (generated)
```

For architecture and implementation notes, see [Clearspace/ARCHITECTURE.md](Clearspace/ARCHITECTURE.md).

Please note that this is still in early development. There will be bugs and missing features. I will try to add these and patch bugs when I can. Thank you for trying this out!
