# Making a Clearspace theme

<!-- NEW (theme files): how to make a theme file. The README's Themes section says how to add one. -->

A theme changes Clearspace's colours, fonts and corner shapes. It is one file, an ordinary zip, that sits
in the **Themes** folder next to `Clearspace.exe` (**Settings > Choose a theme… > Open themes folder**). The
themes that come with Clearspace are in the same folder and are made the same way, so any of them can be
your starting point.

## The quick way

1. Open the Themes folder, copy `Dark.zip` (or whichever theme is closest to what you want) and unzip
   the copy into a folder there, for example `Themes\Sunset`. Clearspace reads a folder exactly as it
   reads a zip, and a folder is easier to work on.
2. Open `Sunset\theme.json` in a text editor and change `name` to what Clearspace should call your theme.
3. In Clearspace, open **Settings > Choose a theme…** and click it under **Your themes**.
4. Open `Sunset\theme.xaml` and change the colours. Each time you save, Clearspace shows the result at
   once. If you save a mistake, the look stays as it was and the status bar says which line is wrong.
5. When it is done, zip the folder's contents as `Sunset.zip`. That one file is the theme: send it to
   someone, and they add it with **Import…** on the same page.

The file's name is how Clearspace tells themes apart, so give yours a name of its own. If a folder and a
zip in the Themes folder have the same name, the folder is the one Clearspace uses.

## What is in a theme file

| In the zip | What it is |
| --- | --- |
| `theme.json` | The theme's name and a few switches. |
| `theme.xaml` | The colours, fonts and corner sizes. |
| `fonts\` | Optional. Font files (`.ttf`, `.otf`, `.ttc`) the theme brings with it. |
| `images\` | Optional. Pictures (`.png`, `.jpg`, `.bmp`, `.gif`) the theme paints with. |

Anything else in the zip (a licence text, your notes) is ignored. If you zip the folder itself, so that
everything sits one folder down inside the zip, that works too.

## theme.json

```json
{
  "format": 1,
  "name": "Sunset",
  "author": "Your name",
  "description": "Warm oranges on deep purple.",
  "icons": "System",
  "crispText": false,
  "fadeToolbar": false,
  "eInk": false
}
```

Everything is optional. Left out, `name` is the file's name and the rest are as shown above.

| Setting | What it does |
| --- | --- |
| `format` | The layout of the theme file. It is `1`. |
| `name` | What the Themes page calls the theme. |
| `author`, `description` | Shown when the pointer rests on the theme there. |
| `icons` | `System` keeps Windows' own icons. Or one of the sets Clearspace draws itself: `Pixel`, `OneBit`, `OneBitDark`, `Ink`, `Line`, `Neon`, `Sketch`, `Tile`, `Dot`, `Mark`. |
| `crispText` | `true` draws text with hard pixel edges, for pixel fonts. |
| `fadeToolbar` | `true` fades the toolbar while the pointer is elsewhere. |
| `eInk` | `true` shows the window as an e-ink panel: sixteen greys, and a fade when the page changes. |

## theme.xaml

Each line gives one part of Clearspace its colour, font or size. The word after `x:Key` says which part.

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <SolidColorBrush x:Key="Base"    Color="#FF2A1533" />
    <SolidColorBrush x:Key="Surface" Color="#FF351C40" />
    <SolidColorBrush x:Key="Ink"     Color="#FFFFE9D6" />
    <SolidColorBrush x:Key="Accent"  Color="#FFFF8A3D" />

    <FontFamily x:Key="UIFont">Segoe UI</FontFamily>
    <CornerRadius x:Key="R8">8</CornerRadius>

</ResourceDictionary>
```

Colours are written `#AARRGGBB`: two digits each for how solid the colour is (`FF` is fully solid, `00`
is see-through), then red, green and blue.

**Anything you leave out keeps the Dark theme's value.** So a theme can be as short as the one above, but
a light theme needs nearly every colour, or dark pieces will show through. `Dark.zip` has every key, each
with a note saying what it colours; that is the best list to work from.

### The keys

| Keys | What they are for |
| --- | --- |
| `Base`, `Surface`, `Overlay` | The window's background; panels on it (the sidebar, cards); menus and pop-ups. |
| `Hover`, `Pressed`, `Selected` | A row or button under the pointer, held down, and selected. |
| `Line`, `LineFaint` | Dividing lines. |
| `EdgeRaised`, `EdgeSunken` | Outlines of buttons, menus and cards; outlines of text boxes and the file list. |
| `Ink`, `InkMuted`, `InkFaint`, `InkDisabled` | Text, from the strongest to the weakest. |
| `Accent`, `AccentSoft`, `AccentWash`, `AccentInk` | The highlight colour; two see-through strengths of it; text on top of a solid accent. |
| `Danger`, `Good`, `Warn`, `Bad`, `Quiet` | Status colours. |
| `MenuHover`, `MenuHoverInk`, `MenuHoverMuted`, `MenuHoverFaint`, `MenuHoverAccent` | The highlighted row of a menu, and the text colours on it. |
| `ThumbRest`, `ThumbHover` | Scroll bars. |
| `Tint`, `ChipHover`, `Divider`, `RowAlt`, `Track`, `SliderRail` | Status bar chips, chips under the pointer, menu separators, every other row in Music folders, the drive usage bar, sliders. |
| `Well`, `BadgeFill`, `NavFill`, `Scrim` | Behind a tile's picture; badges on tiles; the photo viewer's buttons and its backdrop. |
| `CardFill`, `CardEdge`, `Ground3D` | Tooltips and hover cards; the floor of the 3D disk usage views. |
| `UIFont`, `SerifFont`, `DisplayFont`, `MonoFont` | Fonts: the interface, file names, titles, and paths and code. |
| `R2`, `R3`, `R4`, `R5`, `R6`, `R7`, `R8`, `R9`, `R10`, `R12`, `RPlayerBar` | Corner sizes, by how round the corner is in the Dark theme. Set them all to `0` for square corners; the window's own corners follow `R8`. |
| `SelectedRow.Ink`, `SelectedRow.InkMuted`, … | A colour to use instead inside a selected row. For themes whose selection is a solid block that the usual text colour would vanish on: `SelectedRow.` in front of any colour key. |
| `Theme.CaptionColor`, `Theme.CaptionTextColor` | The title bar and its text (Windows 11). Written `<Color x:Key="Theme.CaptionColor">#FF2A1533</Color>`. |
| `Theme.BaseColor` | Needed when `Base` is not one flat colour: the colour Clearspace should treat as the background. It decides the dark or light title bar. |

A few extras that most themes leave alone: `OverlayFill` with `OverlayVisibility` (a see-through pattern
over the whole window, such as scanlines), `FunctionKeysVisibility` (a function-key bar along the
bottom), `BindingFill` with `BindingVisibility` and `PageInset` (a strip down the left edge and the room
left for it), and `NoteTilt` (tooltips that sit slightly crooked). The Neon HUD, DOS Commander and
Sketchbook themes show how each is used.

<!-- NEW (list backdrop) -->
`ListFill` is a picture or pattern behind the file list, and only there. File names are drawn on top of
it, and it does not appear on the other pages (disk usage, themes, backup), which `OverlayFill` covers
because it lies over the whole window. To pin one picture to the bottom right-hand corner of the list
at its own size:

```xml
<ImageBrush x:Key="ListFill" ImageSource="images/map.png"
            Stretch="None" AlignmentX="Right" AlignmentY="Bottom" />
```

Keep it dark or faint enough that file names can still be read over it.

A key Clearspace does not know is ignored.

### More than flat colours

A colour key can hold a gradient, a repeated pattern, or a picture:

```xml
<LinearGradientBrush x:Key="Base" StartPoint="0,0" EndPoint="0,1">
    <GradientStop Color="#FF0A2238" Offset="0" />
    <GradientStop Color="#FF145A5C" Offset="1" />
</LinearGradientBrush>

<ImageBrush x:Key="Surface" ImageSource="images/paper.png"
            TileMode="Tile" Viewport="0,0,256,256" ViewportUnits="Absolute" />
```

`images/paper.png` is `paper.png` in the theme's `images` folder. Picture names may use letters,
digits, spaces and `- _ . ( )`. The Glass, 1-bit Mac and Sketchbook themes have more examples, including
patterns drawn with `DrawingBrush`.

These keys must stay one flat colour (a `SolidColorBrush`), because Clearspace also uses them for the
things it draws itself, such as the disk usage map: `Ink`, `InkMuted`, `InkFaint`, `Accent`, `AccentSoft`,
`AccentWash`, `Good`, `Warn`, `Bad`, `Quiet`, `CardFill`, `CardEdge` and `Ground3D`.

### Fonts

Name an installed font by its name. Further names after a comma are used where the first font is missing
or lacks a letter:

```xml
<FontFamily x:Key="UIFont">Bahnschrift, Segoe UI</FontFamily>
```

To bring a font with the theme, put its file in the theme's `fonts` folder and name it by its family
name (the name Windows shows for the font, not the file's name) after `fonts/#`:

```xml
<FontFamily x:Key="UIFont">fonts/#Pixelify Sans, Segoe UI</FontFamily>
```

Check a font's licence before you share it inside a theme, and include the licence text if it asks you to.

## What a theme cannot do

A theme only changes how Clearspace looks. Before a theme file is used, Clearspace checks that
`theme.xaml` holds nothing but brushes, colours, shapes, fonts, sizes and transforms with plain values,
and refuses the theme otherwise. So these are not allowed, and a theme from someone else cannot use them
either:

- anything in `{ }` (`{Binding …}`, `{StaticResource …}`, `{x:Static …}`);
- styles, templates, controls, animations, or any element not listed here: `SolidColorBrush`,
  `LinearGradientBrush`, `RadialGradientBrush`, `GradientStop`, `DrawingBrush`, `ImageBrush`,
  `DrawingGroup`, `GeometryDrawing`, `GeometryGroup`, `RectangleGeometry`, `EllipseGeometry`,
  `LineGeometry`, `PathGeometry`, `Pen`, `DashStyle`, `RotateTransform`, `ScaleTransform`,
  `TranslateTransform`, `SkewTransform`, `MatrixTransform`, `TransformGroup`, `Color`, `CornerRadius`,
  `Thickness`, `Visibility`, `FontFamily`;
- file paths, web addresses, or any other way of naming a file than `fonts/#Name` and `images/name.png`;
- other namespaces than the two every theme starts with.

A theme that breaks one of these rules, or gives a key the wrong kind of value (a colour where a corner
size belongs), is listed greyed out or refused when picked, with the reason and the line it is on.

## Limits

`theme.xaml` up to 8 MB; up to 64 fonts and pictures, 16 MB each and 64 MB together.
