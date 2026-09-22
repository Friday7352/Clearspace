using System.IO;

namespace Clearspace.Services;

// REWRITTEN (round 2): blocks are colored by *branch*, not by file type.
// Each item directly inside the folder you are looking at gets its own hue, and everything
// inside it shares that hue in slightly varied shades. That answers "which block is which"
// at a glance: one color per labeled block. The list uses the same hue per row.
// (File-type colors made most real drives - game data, caches - one uniform grey.)
internal static class DiskUsagePalette
{
    // Shared with the map's layout: folders with more than DirectLimit items show the largest
    // NamedLimit individually and group the rest into "smaller items" blocks.
    public const int DirectLimit = 150;
    public const int NamedLimit = 120;

    // Distinct, medium-lightness hues that read well on the dark theme and under white text.
    internal static readonly string[] Branches =
        ["#4F86B8", "#C47A45", "#4F9E73", "#A56CA3", "#BE9C3F", "#3F9CA3", "#B85C5A", "#7C80C4", "#8D9B4A", "#C46D8B"];

    public const string GroupColor = "#5E5A54";   // "N smaller items" blocks
    public const string EmptyColor = "#48443F";   // zero-byte rows in the list

    public static string BranchColor(int rank) => Branches[((rank % Branches.Length) + Branches.Length) % Branches.Length];

    // List row color for the item at `rank` in a folder's size-sorted children.
    public static string ListColor(DiskUsageItem item, int rank, int count)
        => item.Bytes == 0 ? EmptyColor : count > DirectLimit && rank >= NamedLimit ? GroupColor : BranchColor(rank);

    // File types are still named in the hover card.
    private static readonly (string Name, string[] Extensions)[] Kinds =
    [
        ("Video", ["mp4", "mkv", "avi", "mov", "wmv", "webm", "m4v", "flv", "mpg", "mpeg", "m2ts", "vob"]),
        ("Image", ["jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif", "raw", "cr2", "cr3",
            "nef", "arw", "dng", "psd", "svg", "ico", "avif", "exr", "hdr", "tga", "dds"]),
        ("Audio", ["mp3", "flac", "wav", "aac", "ogg", "m4a", "wma", "opus", "aiff", "aif", "alac", "mid", "midi", "wem", "bank"]),
        ("Archive", ["zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "zst", "iso", "cab", "img", "dmg", "wim", "esd"]),
        ("Program", ["exe", "dll", "msi", "msix", "appx", "sys", "so", "dylib", "ocx", "drv", "efi", "msp", "pdb"]),
        ("Document", ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "md", "rtf", "odt", "ods", "odp",
            "csv", "epub", "one", "xps"]),
        ("Code", ["cs", "js", "ts", "tsx", "jsx", "py", "cpp", "c", "h", "hpp", "java", "kt", "go", "rs", "rb", "php",
            "json", "xml", "yml", "yaml", "html", "css", "scss", "xaml", "sln", "csproj", "ps1", "sh", "bat", "cmd", "lua", "shader"]),
        ("Game data", ["pak", "rpf", "xpak", "forge", "vpk", "pck", "ucas", "utoc", "big", "arc", "bik", "unity3d", "assets",
            "bundle", "uasset", "ubulk", "gcf", "bsa", "ba2", "esm", "esp", "sga", "wad"]),
        ("Data", ["db", "sqlite", "mdf", "ldf", "pst", "ost", "vhd", "vhdx", "vmdk", "vdi", "qcow2", "dat", "bin",
            "log", "cache", "blob", "bak", "tmp", "etl", "evtx", "edb", "lock", "idx"]),
    ];

    // Built with TryAdd so an extension listed twice can never fail the type initializer.
    private static readonly Dictionary<string, string> KindByExtension = BuildLookup();

    private static Dictionary<string, string> BuildLookup()
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, extensions) in Kinds)
            foreach (var extension in extensions)
                lookup.TryAdd(extension, name);
        return lookup;
    }

    public static string CategoryName(DiskUsageItem item)
    {
        if (item.Id < 0) return "Smaller items";
        if (item.IsFolder) return "Folder";
        var extension = Path.GetExtension(item.Name);
        return extension.Length > 1 && KindByExtension.TryGetValue(extension[1..], out var kind) ? kind : "File";
    }
}
