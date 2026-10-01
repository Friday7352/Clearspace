// Clearspace | What Windows and installed apps offer on a folder's empty-area right-click menu.
// NEW (empty-area menu): two things Explorer shows there that Clearspace now shows too, in its own themed menu:
//   1. Entries other apps add ("Open Git Bash here", "Open with Visual Studio", "Open with Code" ...).
//      Apps that register a plain command in the registry are listed and can be run. Apps that plug in
//      with a shell-extension DLL or a packaged app handler instead (PowerRename, 7-Zip's cascading menu)
//      have no command to read, so they are left out.
//   2. The "New" file templates (Text Document, Bitmap image, Word document ...), read from the same
//      registry entries Explorer uses.
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Clearspace.Services;

// One app entry: the text to show, the command line to run, and a file to take the icon from (or null).
internal sealed record AppMenuEntry(string Label, string Command, string? IconPath);

// One "New >" entry: the extension, the type's name, and what the new file starts as: a copy of a
// template file, some fixed bytes, or (both null) an empty file.
internal sealed record NewFileTemplate(string Extension, string Label, string? TemplateFile, byte[]? Data);

internal static class ShellMenuService
{
    // ---------------------------------------------------------------- entries added by other apps

    // The entries registered for "right-click on the empty part of a folder". includeExtended adds the
    // ones Explorer only shows while Shift is held.
    internal static IReadOnlyList<AppMenuEntry> BackgroundVerbs(bool includeExtended)
    {
        var verbs = new List<AppMenuEntry>();
        try
        {
            using var shell = Registry.ClassesRoot.OpenSubKey(@"Directory\Background\shell");
            if (shell is null)
                return verbs;

            foreach (var name in shell.GetSubKeyNames())
            {
                try
                {
                    using var key = shell.OpenSubKey(name);
                    if (key is null)
                        continue;

                    // Switched off, hidden from menus, or one of Windows' own entries it hides by itself.
                    if (key.GetValue("LegacyDisable") is not null || key.GetValue("ProgrammaticAccessOnly") is not null
                        || key.GetValue("HideBasedOnVelocityId") is not null)
                        continue;
                    if (!includeExtended && key.GetValue("Extended") is not null)
                        continue;

                    using var commandKey = key.OpenSubKey("command");
                    var command = commandKey?.GetValue(null) as string;
                    if (string.IsNullOrWhiteSpace(command))
                        continue; // run by a shell extension, not a command line: nothing Clearspace can start

                    var label = (Resolve(key.GetValue("MUIVerb") as string) ?? Resolve(key.GetValue(null) as string) ?? name)
                        .Replace("&", string.Empty).Trim();
                    if (label.Length == 0)
                        continue;

                    var icon = IconFileOf(key.GetValue("Icon") as string) ?? ExistingFile(Split(Environment.ExpandEnvironmentVariables(command)).File);
                    verbs.Add(new AppMenuEntry(label, command, icon));
                }
                catch (Exception)
                {
                    // one unreadable entry must not hide the others
                }
            }
        }
        catch (Exception)
        {
        }

        verbs.Sort((left, right) => string.Compare(left.Label, right.Label, StringComparison.CurrentCultureIgnoreCase));
        return verbs;
    }

    // Starts an entry for the given folder. Throws if the program cannot be started.
    internal static void Run(AppMenuEntry verb, string folder)
    {
        var command = Environment.ExpandEnvironmentVariables(verb.Command);
        // %V, %L, %W and %1 all stand for "the folder" on this menu.
        foreach (var token in new[] { "%V", "%v", "%L", "%l", "%W", "%w", "%1" })
            command = command.Replace(token, folder);

        var (file, arguments) = Split(command);
        Process.Start(new ProcessStartInfo
        {
            FileName = file,
            Arguments = arguments,
            WorkingDirectory = folder,
            UseShellExecute = false
        })?.Dispose();
    }

    // Splits a command line into the program and the rest.
    private static (string File, string Arguments) Split(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var close = command.IndexOf('"', 1);
            if (close > 0)
                return (command[1..close], command[(close + 1)..].Trim());
        }

        // Not quoted: the program ends at ".exe" when there is one (its path may contain spaces),
        // otherwise at the first space.
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0)
            return (command[..(exe + 4)], command[(exe + 4)..].Trim());

        var space = command.IndexOf(' ');
        return space < 0 ? (command, string.Empty) : (command[..space], command[(space + 1)..].Trim());
    }

    // An "Icon" value looks like  "C:\Program Files\Git\git-bash.exe",0  - keep just the file.
    private static string? IconFileOf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = Environment.ExpandEnvironmentVariables(value).Trim();
        var comma = text.LastIndexOf(',');
        if (comma > 0 && int.TryParse(text[(comma + 1)..].Trim(), out _))
            text = text[..comma];
        return ExistingFile(text.Trim().Trim('"'));
    }

    private static string? ExistingFile(string path)
    {
        try { return File.Exists(path) ? path : null; }
        catch (Exception) { return null; }
    }

    // Registry text is either plain or a pointer into a DLL's translations ("@shell32.dll,-8506").
    private static string? Resolve(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (text[0] != '@')
            return text;

        var buffer = new StringBuilder(512);
        return SHLoadIndirectString(text, buffer, buffer.Capacity, IntPtr.Zero) == 0 && buffer.Length > 0 ? buffer.ToString() : null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder buffer, int bufferLength, IntPtr reserved);

    // ---------------------------------------------------------------- "New" file templates

    private static volatile IReadOnlyList<NewFileTemplate>? _templates;

    // Reads the templates in the background so the first right-click does not wait for the registry.
    internal static void Warm() => Task.Run(() => Templates);

    // The file types Windows offers under "New". Read once and kept.
    internal static IReadOnlyList<NewFileTemplate> Templates => _templates ??= LoadTemplates();

    private static IReadOnlyList<NewFileTemplate> LoadTemplates()
    {
        var found = new Dictionary<string, NewFileTemplate>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var extension in TemplateExtensions())
            {
                try
                {
                    if (!found.ContainsKey(extension) && ReadTemplate(extension) is { } template)
                        found[extension] = template;
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
        }

        // There is always a plain text file to make, whatever the registry says.
        if (!found.ContainsKey(".txt"))
            found[".txt"] = new NewFileTemplate(".txt", "Text Document", null, null);

        return found.Values
            .OrderBy(template => template.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    // Explorer keeps the list of types on its New menu under the signed-in user. When that list is
    // missing, every registered extension is checked instead (slower, same result).
    private static IEnumerable<string> TemplateExtensions()
    {
        try
        {
            using var cache = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Discardable\PostSetup\ShellNew");
            if (cache?.GetValue("Classes") is string[] classes && classes.Length > 0)
                return classes.Where(name => name.Length > 1 && name[0] == '.').ToArray();
        }
        catch (Exception)
        {
        }

        return Registry.ClassesRoot.GetSubKeyNames().Where(name => name.Length > 1 && name[0] == '.').ToArray();
    }

    private static NewFileTemplate? ReadTemplate(string extension)
    {
        using var extensionKey = Registry.ClassesRoot.OpenSubKey(extension);
        if (extensionKey is null)
            return null;

        // The template is described under .ext\ShellNew, or under .ext\<type id>\ShellNew.
        var typeId = extensionKey.GetValue(null) as string;
        using var shellNew = extensionKey.OpenSubKey("ShellNew")
            ?? (string.IsNullOrEmpty(typeId) ? null : extensionKey.OpenSubKey(typeId + @"\ShellNew"));
        if (shellNew is null)
            return null;

        var templateFile = TemplatePath(shellNew.GetValue("FileName") as string);
        var data = shellNew.GetValue("Data") switch
        {
            byte[] bytes => bytes,
            string text => Encoding.UTF8.GetBytes(text),
            _ => null
        };
        var empty = shellNew.GetValue("NullFile") is not null;

        // "Command" entries (Shortcut, Briefcase) start a wizard instead of making a file; skipped.
        if (!empty && templateFile is null && data is null)
            return null;

        return new NewFileTemplate(extension, TypeName(typeId, extension), templateFile, data);
    }

    // A template named without a folder lives in one of Windows' template folders.
    private static string? TemplatePath(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        name = Environment.ExpandEnvironmentVariables(name);
        if (Path.IsPathRooted(name))
            return ExistingFile(name);

        string[] folders =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ShellNew"),
            Environment.GetFolderPath(Environment.SpecialFolder.Templates),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonTemplates)
        ];
        return folders.Select(folder => ExistingFile(Path.Combine(folder, name))).FirstOrDefault(path => path is not null);
    }

    // "Text Document" for .txt: the type's display name, or "TXT File" when it has none.
    private static string TypeName(string? typeId, string extension)
    {
        if (!string.IsNullOrEmpty(typeId))
        {
            try
            {
                using var type = Registry.ClassesRoot.OpenSubKey(typeId);
                var name = Resolve(type?.GetValue("FriendlyTypeName") as string) ?? Resolve(type?.GetValue(null) as string);
                if (!string.IsNullOrWhiteSpace(name))
                    return name.Trim();
            }
            catch (Exception)
            {
            }
        }

        return extension.TrimStart('.').ToUpperInvariant() + " File";
    }

    // ---------------------------------------------------------------- making the new item

    // Creates "New folder" (or "New folder (2)" ...) and returns its path.
    internal static string CreateFolder(string parent)
    {
        var path = FreeName(parent, "New folder", string.Empty);
        Directory.CreateDirectory(path);
        return path;
    }

    // Creates "New Text Document.txt" (or "... (2).txt") from the template and returns its path.
    internal static string CreateFile(NewFileTemplate template, string parent)
    {
        var path = FreeName(parent, "New " + template.Label, template.Extension);
        if (template.TemplateFile is { } source && File.Exists(source))
            File.Copy(source, path);
        else
            File.WriteAllBytes(path, template.Data ?? []);
        return path;
    }

    private static string FreeName(string parent, string name, string extension)
    {
        // Type names can hold characters a file name cannot ("C/C++ Source").
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, ' ');

        var path = Path.Combine(parent, name + extension);
        for (var number = 2; Directory.Exists(path) || File.Exists(path); number++)
            path = Path.Combine(parent, $"{name} ({number}){extension}");
        return path;
    }
}
