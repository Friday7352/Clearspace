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

### See what is indexed

Open **Indexing** from the file browser toolbar, the disk viewer toolbar, or the
index status indicator. This page stays inside Clearspace and shows:

- The drive and folder currently being scanned, with live entry and folder counts.
- Each drive's coverage status and the files and folders actually in its index.
- During a scan, per-drive progress bars show the percentage of discovered folders processed,
  with processed and waiting counts. More folders are discovered during scanning,
  so the percentage can decrease. Completed drives show a ready status instead of
  an idle progress bar. Excluded or unreadable locations remain listed separately.
  No extra counting scan is required.
- Waiting drives explain the automatic 20-minute rescan cooldown and retry time,
  work already running, or errors. **Scan now** bypasses the cooldown and requests
  a scan on the existing background worker; current saved entries stay available.
  **Refresh status** only refreshes the display. A ready index does not need a
  continuous full scan because file changes are tracked in the background.
- Skipped folders and reasons from the latest scan in this session. Older saved
  indexes clearly indicate when those details are unavailable.
- The saved index's size on disk, its last save time, and its full storage path,
  with buttons to copy the path or open the containing folder.
- An estimated memory footprint, listed separately from disk space.

The index browser supports folder navigation, filtering, and pages of up to 1,000
entries so large folders do not create enormous interface lists. Index reads run
in the background. Clearspace indexes names, paths, sizes, and file metadata;
document-content indexing belongs to Windows Search, whose options are linked
from the page. Use **Back** to return to the view you were using.

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

Keep zooming out past the whole drive's files and the rest of the drive comes into
view at the same scale: its free space, and used space the index does not account
for. Zoom out further still and the other drives in the drive list appear in a row
beside it, each sized to its capacity, with a name plate on top and a cable running
down to your computer below them. Indexed drives show their own folders and files,
laid out like the map; drives not indexed yet show used and free space. Zoom into
another drive and it opens once its files fill the view, or click it (or one of its
folders) to fly in and open it there. The drives keep their places in drive-letter
order whichever one is open. Click anywhere else out there to return to the files.

Each drive is drawn as the device it is - an M.2 NVMe stick with its gold contacts,
a 2.5" SATA SSD, a 3.5" hard drive with its connectors, a USB enclosure - read from
Windows' own description of the disk. Zoomed all the way out, each one's cover fades
in: a hard drive's lid with its platter and label, an SSD's or NVMe stick's label,
all showing the maker, model and capacity. Zooming into a drive opens its cover up
again to show the files inside. Network drives sit on a server of their own, named
after it and cabled to your computer.

Zooming out stops once at the row of drives on their own; a few more pushes of the
wheel carry on out to your actual motherboard below them, each drive cabled down into
a SATA port on its edge. The board is drawn the way the map draws everything - flat
blocks with the map's gaps and labels: the board tagged with its maker and model, the
processor, a memory block holding its slots (a module in each one that is really
filled, labelled with its size), and the graphics card, all named from the machine
itself. Network drives sit on their servers beside it, cabled to the board. Hover a
part for its details.

Zoom into the memory and its sticks fade away to show what it holds, laid out like a
drive: every running program sized by the memory it takes (all its processes
together), then Windows' own share, what is available, and what the hardware
reserves. Zoom into a program and you see what it has loaded - its .exe, its
libraries, data files it has mapped - each sized by how much of it is actually in
memory, beside its own working data. Click any block to zoom to it. It updates live,
once a second, but only while you are looking into the memory (its contents on screen
and the window not minimized) - anywhere else nothing is read or redrawn. Blocks grow
and shrink in place as programs use more or less memory, rather than reshuffling. It
needs no administrator rights; a few system programs Windows keeps
closed to other programs show only their size. Memory is not stored per stick
(Windows spreads every program across all of them), so the programs fill the memory
as a whole.

Zoom into the processor and its lid fades away to show the die, laid out as your chip
is actually built - read from Windows, not a stock picture: its core complexes (one
per L3 cache, so a Ryzen shows its CCXs), the cores in them with their threads and
their own L1 and L2 caches, performance and efficiency cores on hybrid Intel chips
(efficiency cores in the fours that share an L2), and the L3 each complex shares.
Every core and thread is lit from cool to warm by how busy it is, with its clock
speed in its details. Beside the die, What's running shows every program sized by
its share of the processor. Like the memory, it updates once a second only while you
are looking into it, and needs no administrator rights (temperatures would need a
driver, so they are not shown). It is drawn as the chip is organised, not to scale.

**Index network drives** (in the same gear panel, off by default) indexes mapped
network drives too, so a NAS or share appears in the drive list, in the map, beside
your other drives when zoomed out, and in search. Clearspace only reads a share while
it answers - reachability is checked in the background once a minute, never on the
UI thread - and a share that is out of reach keeps its last index so its sizes can
still be browsed. The first pass reads the whole share over the network, so a large
NAS can take a while. Turning the option off drops those indexes straight away.

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

**Experimental views** (also in the gear panel, off by default) add a **View** picker
beside the drive list. The map itself is untouched: it is only hidden while another
view shows, keeps following navigation, and comes back exactly as you left it.
*3D blocks* raises the folder you are in as a field of blocks on the map's own
layout - orbit with a drag, pan with a right-drag, scroll to zoom, double-click a
folder to open it. What the height means is chosen in the corner: *Stacked folders*
(every folder a slab with its contents standing on it), *size*, *file count*, or
*file types* (coloured by type, as tall as that type's share of the folder).
*Disk layout* shows where the largest files physically sit on the drive, read from
the file system the way a defragmenter reads it, as a spinning platter (cluster 0
at the rim, the end of the drive at the hub) or as a defragmenter-style grid. Items
in the current folder keep their list colours and everything else is grey; hover
for the file and how many pieces it is in, click to open it. Free space is shown
when Clearspace runs as administrator. On an SSD the positions are logical: the
drive decides where data physically lives.

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
