// Clearspace | NEW (search trace): a small log of searches that took long, and of the moments a drive
// dropped out of the live index. A slow search can have several causes (the index scan, files being
// checked on the disk, a drive being crawled because the index lost track of it, the window being busy),
// and which one it was can't be told from the outside. Each slow search leaves one line here with how long
// every stage took, so the cause can be read off instead of guessed.
// The log is %APPDATA%\Clearspace\search-trace.log, on this PC only, and is kept under 200 KB (the older
// half is dropped when it grows past that). It records timings and counts; never what was typed.
// Only searches of 1.5 seconds or more are written, plus things that never reach that line because the
// search is replaced by the next key press first: a key press whose own handling took 150 ms or more, a
// list that took 50 ms or more to draw after it was replaced (ListDrawn), and any moment the window
// stopped responding for 400 ms or more, with what it was busy with (WatchWindow).
using System.IO;

namespace Clearspace.Services;

internal static class SearchTrace
{
    internal static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(1500);
    private const long MaxBytes = 200 * 1024;
    private static readonly Lock Gate = new();

    // What the search was last doing on the UI thread, and when it started doing it. Written by
    // SearchCoordinator; read by the watch below when the window stops responding.
    private static volatile string _step = "nothing yet";
    private static long _stepAt = System.Diagnostics.Stopwatch.GetTimestamp();
    private static int _watching;

    // NEW (search fix): what the UI thread is doing at this moment. A label (string), or the queued task
    // the window is running (named only if it is ever sampled, so that keeping this up to date costs one
    // write). Written on the UI thread; read by the watch.
    private const string Idle = "nothing (waiting for Windows)";
    private static volatile object _doing = Idle;
    private static int _listsReplaced;

    internal static void Step(string what)
    {
        _step = what;
        _doing = what;
        Volatile.Write(ref _stepAt, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    // NEW (search fix): marks a stretch of work on the UI thread; the label before it comes back when the
    // stretch ends. `using (SearchTrace.During("replacing the list")) { ... }`
    internal static Stretch During(string what)
    {
        var before = _doing;
        _doing = what;
        return new Stretch(before);
    }

    internal readonly struct Stretch(object before) : IDisposable
    {
        public void Dispose() => _doing = before;
    }

    // NEW (search fix): the list was given new contents. Counted, so a freeze can say how many times.
    internal static void ListReplaced() => Interlocked.Increment(ref _listsReplaced);

    // NEW (search fix): how long the list took to be drawn after it was given new contents. Each time, every
    // row on screen is built again, and the letter just typed is not shown until that is done.
    internal static void ListDrawn(double milliseconds, int items, string view)
    {
        if (milliseconds >= 50)
            Write($"drawing the list took {milliseconds:N0} ms after it was replaced ({items:N0} items, {view})");
    }

    // Notices when the window stops responding. A background thread asks the window, five times a second,
    // to run something that takes no time, and measures how long the answer takes. More than 400 ms means
    // the UI thread was busy that long.
    // CHANGED (search fix): while it waits it looks, every 20 ms, at what the UI thread is doing, and the
    // line it writes adds those up ("busy with: drawing 3,100 ms, a key press 600 ms, ..."). It also says
    // how long the garbage collector stopped the app in that time and how often the list was replaced.
    // The first version only named the last search step, which could not tell drawing from the search.
    internal static void WatchWindow(System.Windows.Threading.Dispatcher dispatcher)
    {
        if (Interlocked.Exchange(ref _watching, 1) == 1)
            return; // every Clearspace window shares the one UI thread; one watch covers them all

        WindowWatch.Follow(dispatcher);

        var thread = new Thread(() =>
        {
            try
            {
                var seen = new Dictionary<string, int>();

                while (!dispatcher.HasShutdownStarted)
                {
                    var asked = System.Diagnostics.Stopwatch.GetTimestamp();
                    var paused = GC.GetTotalPauseDuration();
                    var full = GC.CollectionCount(2);
                    var lists = Volatile.Read(ref _listsReplaced);
                    seen.Clear();

                    var answer = dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, static () => { });
                    while (answer.Wait(TimeSpan.FromMilliseconds(WindowWatch.Every)) == System.Windows.Threading.DispatcherOperationStatus.Pending)
                    {
                        var what = WindowWatch.Name(_doing);
                        seen[what] = seen.GetValueOrDefault(what) + 1;
                    }

                    var busy = System.Diagnostics.Stopwatch.GetElapsedTime(asked);

                    if (busy.TotalMilliseconds >= 400)
                    {
                        var since = System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref _stepAt));
                        var with = string.Join(", ", seen.OrderByDescending(pair => pair.Value).Take(8)
                            .Select(pair => $"{pair.Key} {pair.Value * WindowWatch.Every:N0} ms"));
                        Write($"the window did not respond for {busy.TotalMilliseconds:N0} ms | busy with: {with}" +
                              $" | garbage collection stopped the app for {(GC.GetTotalPauseDuration() - paused).TotalMilliseconds:N0} ms ({GC.CollectionCount(2) - full} full)" +
                              $" | the list was replaced {Volatile.Read(ref _listsReplaced) - lists} times" +
                              $" | last search step: {_step}, {since.TotalMilliseconds:N0} ms before it responded again");
                    }

                    Thread.Sleep(200);
                }
            }
            catch (Exception)
            {
                // The window is closing, or the watch could not run: there is nothing to record.
            }
        })
        {
            IsBackground = true,
            Name = "Clearspace window watch",
            Priority = ThreadPriority.AboveNormal
        };
        thread.Start();
    }

    // NEW (search fix): keeps `_doing` up to date from the window's own events. Kept apart from the rest so
    // that nothing here is loaded until a window asks for the watch (the tests use SearchTrace without one).
    private static class WindowWatch
    {
        internal const int Every = 20; // ms between looks at the UI thread

        private static readonly object Message = new();
        private static int _message;
        private static readonly Dictionary<System.Windows.RoutedEvent, string> Inputs = [];
        private static readonly System.Reflection.PropertyInfo? TaskName = typeof(System.Windows.Threading.DispatcherOperation)
            .GetProperty("Name", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        // On the UI thread. Every handler is one or two field writes: they run for every message, every
        // input event and every queued task of the window.
        internal static void Follow(System.Windows.Threading.Dispatcher dispatcher)
        {
            try
            {
                dispatcher.Hooks.OperationStarted += (_, e) => _doing = e.Operation;
                dispatcher.Hooks.OperationCompleted += (_, _) => _doing = Idle;
                dispatcher.Hooks.OperationAborted += (_, _) => _doing = Idle;
                dispatcher.Hooks.DispatcherInactive += (_, _) => _doing = Idle;

                System.Windows.Interop.ComponentDispatcher.ThreadPreprocessMessage += (ref System.Windows.Interop.MSG message, ref bool _) =>
                {
                    _message = message.message;
                    _doing = Message;
                };

                var input = System.Windows.Input.InputManager.Current;
                input.PreProcessInput += (_, e) =>
                {
                    var routed = e.StagingItem.Input.RoutedEvent;
                    if (routed is null)
                        return;
                    if (!Inputs.TryGetValue(routed, out var label))
                        Inputs[routed] = label = $"input: {routed.Name}";
                    _doing = label;
                };
                input.PostProcessInput += (_, _) => _doing = "after an input event (still inside its Windows message)";
            }
            catch (Exception)
            {
                // Without these the watch still reports the freeze and the last search step.
            }
        }

        // On the watch thread: a name for what the UI thread was doing when it was looked at.
        internal static string Name(object doing)
        {
            try
            {
                if (ReferenceEquals(doing, Message))
                    return $"Windows message {MessageName(Volatile.Read(ref _message))}";

                return doing switch
                {
                    string label => label,
                    System.Windows.Threading.DispatcherOperation task => TaskLabel(task),
                    _ => "something else"
                };
            }
            catch (Exception)
            {
                return "something that could not be named";
            }
        }

        private static string TaskLabel(System.Windows.Threading.DispatcherOperation task)
        {
            var name = TaskName?.GetValue(task) as string ?? "a queued task";

            // The two that matter most, in words. Layout (building and measuring rows) and drawing both
            // happen inside the first; ListDrawn times the list's share of it.
            if (name.Contains("MediaContext.RenderMessageHandler", StringComparison.Ordinal) ||
                name.Contains("ContextLayoutManager", StringComparison.Ordinal))
                return "laying out and drawing the window";

            const string prefix = "System.Windows.";
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                name = name[prefix.Length..];
            if (name.Length > 110)
                name = name[..110];
            return $"queued task {name} ({task.Priority})";
        }

        private static string MessageName(int message) => message switch
        {
            0x000F => "paint",
            0x0100 => "key down",
            0x0101 => "key up",
            0x0102 => "character typed",
            0x0104 => "system key down",
            0x0113 => "timer",
            0x0200 => "mouse move",
            0x0201 => "left button down",
            0x0202 => "left button up",
            0x0204 => "right button down",
            0x020A => "mouse wheel",
            _ => $"0x{message:X4}"
        };
    }

    internal static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clearspace", "search-trace.log");

    // One line, with the time in front. Never throws: the log is a diagnostic, not a feature.
    // CHANGED (search fix): the file is written from the thread pool. Some lines are reported from the UI
    // thread, and a diagnostic should not make the window wait for the disk. The time in front is the
    // time of the call.
    internal static void Write(string line)
    {
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}{Environment.NewLine}";

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                lock (Gate)
                {
                    var path = FilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                    var file = new FileInfo(path);
                    if (file.Exists && file.Length > MaxBytes)
                    {
                        var lines = File.ReadAllLines(path);
                        File.WriteAllLines(path, lines.Skip(lines.Length / 2));
                    }

                    File.AppendAllText(path, text);
                }
            }
            catch (Exception)
            {
            }
        });
    }
}
