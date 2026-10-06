// Clearspace | NEW (batch rename): works out new names for several files and folders, and renames them.
//  - Plan: every item gets its name from a pattern ({name}, {n}, {date}), find and replace, and a change of
//    case. The extension is kept. Anything that can't be done is reported per item, before a file is touched.
//  - Apply: renames the items. Names may be swapped or shifted along (1 -> 2, 2 -> 3); those go through a
//    temporary name first. If one rename fails, the ones already done are put back.
// There are no windows in here: the dialog is BatchRenameWindow.cs, the tests Clearspace.Tests/BatchRenameTests.cs.
using System.Globalization;
using System.IO;
using System.Text;

namespace Clearspace.Services;

internal enum RenameCase { Unchanged, Lower, Upper, Title }

internal sealed record RenameOptions
{
    // The new name without its extension. {name} = the current name, {n} = a counter ({n:3} = 001),
    // {date} = the date modified (2026-10-02). A pattern with neither {name} nor {n} gets " ({n})" added,
    // as Explorer does, so the items don't all end up with the same name.
    public string Pattern { get; init; } = "{name}";
    public string Find { get; init; } = string.Empty;
    public string Replace { get; init; } = string.Empty;
    public bool MatchCase { get; init; }
    public int Start { get; init; } = 1;
    public RenameCase Case { get; init; } = RenameCase.Unchanged;
}

internal sealed record RenameSource(string FullPath, bool IsFolder, DateTime Modified);

// One item of the plan. Problem is null when the item can be renamed (or keeps its name).
internal sealed record RenameStep(RenameSource Source, string OldName, string NewName, string NewPath, string? Problem)
{
    public bool Changes => !string.Equals(OldName, NewName, StringComparison.Ordinal);
}

// Renamed is empty when Error is set: a failed batch is put back as it was.
internal sealed record RenameOutcome(IReadOnlyList<(string From, string To)> Renamed, string? Error);

internal static class BatchRename
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    // The characters Windows does not allow in a name (spelled out, so the rule is the same wherever the
    // tests run), besides control characters.
    private static readonly char[] ForbiddenCharacters = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    // The part of a name the pattern replaces, and the part that stays. Folders have no extension. A locked
    // file keeps both of its endings (photo.jpg.cslock -> "photo" + ".jpg.cslock"), so it still opens as a
    // .jpg after it is unlocked.
    internal static (string Stem, string Extension) SplitName(string name, bool isFolder)
    {
        if (isFolder)
            return (name, string.Empty);

        var inner = LockNames.HasLockedName(name) ? LockNames.Unlocked(name) : name;
        var extension = Path.GetExtension(inner);
        var stem = inner[..^extension.Length];
        return (stem, name[stem.Length..]);
    }

    internal static string NewNameFor(RenameSource source, int index, RenameOptions options)
    {
        var oldName = Path.GetFileName(Path.TrimEndingDirectorySeparator(source.FullPath));
        var (stem, extension) = SplitName(oldName, source.IsFolder);

        if (options.Find.Length > 0)
            stem = stem.Replace(options.Find, options.Replace,
                options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

        var text = Expand(EffectivePattern(options.Pattern), stem, options.Start + index, source.Modified).Trim();
        text = options.Case switch
        {
            RenameCase.Lower => text.ToLower(CultureInfo.CurrentCulture),
            RenameCase.Upper => text.ToUpper(CultureInfo.CurrentCulture),
            RenameCase.Title => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLower(CultureInfo.CurrentCulture)),
            _ => text
        };

        return text + extension;
    }

    internal static string EffectivePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return "{name}";

        return Uses(pattern, "name") || Uses(pattern, "n") ? pattern : pattern.TrimEnd() + " ({n})";
    }

    private static bool Uses(string pattern, string wanted)
    {
        for (var open = pattern.IndexOf('{'); open >= 0; open = pattern.IndexOf('{', open + 1))
        {
            var close = pattern.IndexOf('}', open + 1);
            if (close < 0)
                return false;

            var token = pattern[(open + 1)..close];
            var colon = token.IndexOf(':');
            if (colon >= 0)
                token = token[..colon];

            if (token.Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal static string Expand(string pattern, string stem, int number, DateTime modified)
    {
        var text = new StringBuilder(pattern.Length + stem.Length);

        for (var i = 0; i < pattern.Length; i++)
        {
            var close = pattern[i] == '{' ? pattern.IndexOf('}', i + 1) : -1;
            var value = close < 0 ? null : Token(pattern[(i + 1)..close], stem, number, modified);

            if (value is null)
            {
                text.Append(pattern[i]); // an ordinary character, or a brace that is not one of the tokens
                continue;
            }

            text.Append(value);
            i = close;
        }

        return text.ToString();
    }

    private static string? Token(string token, string stem, int number, DateTime modified)
    {
        var name = token;
        var argument = string.Empty;
        var colon = token.IndexOf(':');
        if (colon >= 0)
        {
            name = token[..colon];
            argument = token[(colon + 1)..];
        }

        switch (name.Trim().ToLowerInvariant())
        {
            case "name":
                return stem;
            case "n":
                var digits = int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var width)
                    ? Math.Clamp(width, 1, 9)
                    : 1;
                return number.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
            case "date":
                return modified == DateTime.MinValue
                    ? string.Empty
                    : modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            default:
                return null;
        }
    }

    // What is wrong with a name, in words for the dialog; null when Windows accepts it.
    internal static string? Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "The name is empty.";
        if (name.IndexOfAny(ForbiddenCharacters) >= 0 || name.Any(char.IsControl))
            return "A name can't contain \\ / : * ? \" < > |";
        if (name.EndsWith(' ') || name.EndsWith('.'))
            return "A name can't end with a space or a full stop.";
        if (name.Length > 255)
            return "The name is too long.";
        if (ReservedNames.Contains(name.Split('.')[0].TrimEnd()))
            return "Windows keeps this name for itself.";

        return null;
    }

    // `exists` answers whether something is at a path right now. It is only asked about names that none
    // of the items being renamed has, so a swap (a -> b, b -> a) is not reported as a clash.
    internal static IReadOnlyList<RenameStep> Plan(IReadOnlyList<RenameSource> sources, RenameOptions options, Func<string, bool> exists)
    {
        var steps = new List<RenameStep>(sources.Count);
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wanted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            var path = Path.TrimEndingDirectorySeparator(source.FullPath);
            var oldName = Path.GetFileName(path);
            var folder = Path.GetDirectoryName(path) ?? string.Empty;
            var newName = NewNameFor(source, i, options);
            var newPath = Path.Combine(folder, newName);

            current.Add(path);
            var problem = folder.Length == 0 ? "This item can't be renamed." : Validate(newName);
            // Nothing left in front of the extension (".jpg"). A name that was like that already may stay.
            if (problem is null && newName != oldName && newName.Length == SplitName(oldName, source.IsFolder).Extension.Length)
                problem = "The name is empty.";
            if (problem is null)
                wanted[newPath] = wanted.GetValueOrDefault(newPath) + 1;

            steps.Add(new RenameStep(source, oldName, newName, newPath, problem));
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Problem is not null)
                continue;

            if (wanted[step.NewPath] > 1)
                steps[i] = step with { Problem = "Another item gets the same name." };
            else if (!current.Contains(step.NewPath) && exists(step.NewPath))
                steps[i] = step with { Problem = "Something with this name is already here." };
        }

        return steps;
    }

    internal static bool CanApply(IReadOnlyList<RenameStep> steps)
        => steps.All(step => step.Problem is null) && steps.Any(step => step.Changes);

    // Renames what the plan changes. `move` is File.Move / Directory.Move; tests pass their own.
    internal static RenameOutcome Apply(IReadOnlyList<RenameStep> steps, Action<string, string>? move = null)
    {
        move ??= MoveOnDisk;
        var work = steps.Where(step => step.Problem is null && step.Changes).ToList();
        if (work.Count == 0)
            return new RenameOutcome([], null);

        // A new name that one of the items still has (a swap, a shift along, or only the capitals changing)
        // can't be taken directly: everything steps aside to a temporary name first.
        var occupied = new HashSet<string>(
            work.Select(step => Path.TrimEndingDirectorySeparator(step.Source.FullPath)), StringComparer.OrdinalIgnoreCase);
        var staged = work.Any(step => occupied.Contains(step.NewPath));

        var done = new List<(string From, string To)>(staged ? work.Count * 2 : work.Count);
        var busy = string.Empty;
        try
        {
            if (staged)
            {
                var token = Guid.NewGuid().ToString("N")[..8];
                var parked = new string[work.Count];
                for (var i = 0; i < work.Count; i++)
                {
                    var from = Path.TrimEndingDirectorySeparator(work[i].Source.FullPath);
                    busy = from;
                    parked[i] = Path.Combine(Path.GetDirectoryName(from)!, $"~clearspace-rename-{token}-{i}");
                    move(from, parked[i]);
                    done.Add((from, parked[i]));
                }

                for (var i = 0; i < work.Count; i++)
                {
                    busy = work[i].Source.FullPath;
                    move(parked[i], work[i].NewPath);
                    done.Add((parked[i], work[i].NewPath));
                }
            }
            else
            {
                foreach (var step in work)
                {
                    var from = Path.TrimEndingDirectorySeparator(step.Source.FullPath);
                    busy = from;
                    move(from, step.NewPath);
                    done.Add((from, step.NewPath));
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var restored = true;
            for (var i = done.Count - 1; i >= 0; i--)
            {
                try { move(done[i].To, done[i].From); }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    restored = false;
                }
            }

            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(busy));
            return new RenameOutcome([], restored
                ? $"{name} could not be renamed, so nothing was changed. {error.Message}"
                : $"{name} could not be renamed, and some items could not be put back under their old names. {error.Message}");
        }

        return new RenameOutcome(
            work.Select(step => (Path.TrimEndingDirectorySeparator(step.Source.FullPath), step.NewPath)).ToList(), null);
    }

    private static void MoveOnDisk(string from, string to)
    {
        if (Directory.Exists(from))
            Directory.Move(from, to);
        else
            File.Move(from, to);
    }
}
