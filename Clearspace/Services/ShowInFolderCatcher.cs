// Clearspace | NEW (show in folder): "Show in folder" / "Show in Explorer" from other programs selects the file.
//
// A program that wants to show you a file (a browser's "Show in folder", an editor's "Reveal in File
// Explorer") asks Windows with SHOpenFolderAndSelectItems. With "Open folders with Clearspace" on, Windows
// then starts Clearspace with only the folder (--open-folder "C:\Downloads"); the file to select is not
// passed. What Windows does next is wait for a folder window showing that folder to appear in its list of
// open windows (ShellWindows), and then tell that window's view which item to select (IShellView.SelectItem).
//
// So while a folder is being opened, this puts a stand-in for a folder window into that list: an invisible
// window registered for the folder, answering just enough of a File Explorer window's interfaces
// (IWebBrowserApp, IServiceProvider, IShellView) for Windows to hand it the item. The item's path is passed
// on (to the running Clearspace, which goes to it and selects it), and the stand-in is taken out of the list
// again. If no program asked for an item, nothing arrives and the stand-in is removed after a few seconds.
// The open-source Files app (files-community/Files, its launcher) does the same.
//
// Everything runs on a thread of its own (a COM single-threaded apartment with its own message loop), so
// the folder opens straight away and does not wait for it. Any failure means only that no file is selected.
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Clearspace.Services;

internal sealed class ShowInFolderCatcher
{
    // How long a program has, after the folder starts opening, to say which item to select.
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private readonly string _folder;
    private readonly Action<string> _found;
    private readonly ManualResetEventSlim _finished = new();
    private Dispatcher? _dispatcher;
    private int _delivered;

    /// <summary>The item Windows asked to select, once it has; null until then.</summary>
    internal string? Caught { get; private set; }

    private ShowInFolderCatcher(string folder, Action<string> found)
    {
        _folder = folder;
        _found = found;
    }

    /// <summary>
    /// Starts listening for the item to select in <paramref name="folder"/>. <paramref name="found"/> is
    /// called at most once, on the catcher's own thread, with the full path of the item.
    /// </summary>
    internal static ShowInFolderCatcher Start(string folder, Action<string> found)
    {
        var catcher = new ShowInFolderCatcher(folder, found);
        var thread = new Thread(catcher.Run) { IsBackground = true, Name = "Clearspace show-in-folder" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return catcher;
    }

    /// <summary>Blocks until an item arrived or the catcher gave up. True when an item arrived.</summary>
    internal bool Wait(TimeSpan timeout)
    {
        _finished.Wait(timeout);
        return Volatile.Read(ref _delivered) == 1;
    }

    private void Run()
    {
        IntPtr folderPidl = IntPtr.Zero;
        HwndSource? window = null;
        IShellWindowsList? windows = null;
        ShowInFolderStandIn? standIn = null;
        int pendingCookie = 0, cookie = 0;
        var registered = false;

        try
        {
            if (SHParseDisplayName(_folder, IntPtr.Zero, out folderPidl, 0, out _) != 0 || folderPidl == IntPtr.Zero)
                return;

            // The folder's address in the form ShellWindows compares: its ID list, as an array of bytes.
            var pidlBytes = new byte[ILGetSize(folderPidl)];
            Marshal.Copy(folderPidl, pidlBytes, 0, pidlBytes.Length);

            // An invisible message-only window: ShellWindows wants one to go with each folder window.
            window = new HwndSource(new HwndSourceParameters("Clearspace show in folder")
            {
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE
                WindowStyle = 0
            });
            _dispatcher = Dispatcher.CurrentDispatcher;

            if (Type.GetTypeFromCLSID(ShellWindowsClass) is not { } type ||
                Activator.CreateInstance(type) is not IShellWindowsList list)
                return;
            windows = list;

            standIn = new ShowInFolderStandIn(this, folderPidl, window.Handle);

            object location = pidlBytes;
            object root = null!; // VT_EMPTY
            if (windows.RegisterPending(GetCurrentThreadId(), ref location, ref root, SWC_BROWSER, out pendingCookie) != 0)
                pendingCookie = 0;
            if (windows.Register(standIn, unchecked((int)window.Handle.ToInt64()), SWC_BROWSER, out cookie) != 0)
                return;
            registered = true;
            windows.OnNavigate(cookie, ref location);

            // Give up after a while; an item arriving stops it sooner (Deliver).
            var timer = new DispatcherTimer { Interval = Window };
            timer.Tick += (_, _) => { timer.Stop(); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); };
            timer.Start();

            Dispatcher.Run(); // the message loop that Windows' calls into the stand-in arrive through
        }
        catch (Exception)
        {
            // Nothing is selected; the folder still opened.
        }
        finally
        {
            try
            {
                if (windows is not null)
                {
                    if (registered) windows.Revoke(cookie);
                    if (pendingCookie != 0 && pendingCookie != cookie) windows.Revoke(pendingCookie);
                }
            }
            catch (Exception) { }

            try { window?.Dispose(); } catch (Exception) { }
            if (windows is not null && Marshal.IsComObject(windows))
                Marshal.ReleaseComObject(windows);
            standIn?.Forget();
            if (folderPidl != IntPtr.Zero)
                Marshal.FreeCoTaskMem(folderPidl);
            _finished.Set();
        }
    }

    // Called by the stand-in (on this catcher's thread) with the item Windows asked to select.
    internal void Deliver(string path)
    {
        if (Interlocked.Exchange(ref _delivered, 1) != 0)
            return;

        Caught = path;
        try { _found(path); }
        catch (Exception) { }

        // Leave the list straight away, so a second "Show in folder" finds the real Clearspace window or a new stand-in.
        Stop();
    }

    // Ends the message loop; Run then takes the stand-in out of the list.
    internal void Stop() => _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Normal);

    // ---- COM declarations

    internal const int S_OK = 0, S_FALSE = 1;
    internal const int E_NOTIMPL = unchecked((int)0x80004001);
    internal const int E_NOINTERFACE = unchecked((int)0x80004002);
    internal const int E_FAIL = unchecked((int)0x80004005);
    private const int SWC_BROWSER = 1;
    internal const uint SVSI_SELECT = 0x1, SVSI_ENSUREVISIBLE = 0x8, SVSI_FOCUSED = 0x10;
    internal const uint SIGDN_FILESYSPATH = 0x80058000;

    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    internal static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    internal static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");
    internal static readonly Guid IID_IOleWindow = new("00000114-0000-0000-C000-000000000046");
    internal static readonly Guid IID_IWebBrowser = new("EAB22AC1-30C1-11CF-A7EB-0000C05BAE0B");

    // IShellWindows, up to OnNavigate (the methods after it are not used, so need not be declared).
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindowsList
    {
        [PreserveSig] int get_Count(out int count);
        [PreserveSig] int Item([MarshalAs(UnmanagedType.Struct)] object index, [MarshalAs(UnmanagedType.IDispatch)] out object? folder);
        [PreserveSig] int _NewEnum([MarshalAs(UnmanagedType.IUnknown)] out object? enumerator);
        [PreserveSig] int Register([MarshalAs(UnmanagedType.IDispatch)] object window, int hwnd, int windowClass, out int cookie);
        [PreserveSig] int RegisterPending(int threadId, [MarshalAs(UnmanagedType.Struct)] ref object location, [MarshalAs(UnmanagedType.Struct)] ref object root, int windowClass, out int cookie);
        [PreserveSig] int Revoke(int cookie);
        [PreserveSig] int OnNavigate(int cookie, [MarshalAs(UnmanagedType.Struct)] ref object location);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern uint ILGetSize(IntPtr pidl);

    [DllImport("shell32.dll")]
    internal static extern IntPtr ILCombine(IntPtr parent, IntPtr child);

    [DllImport("shell32.dll")]
    internal static extern int SHGetNameFromIDList(IntPtr pidl, uint form, out IntPtr name);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();
}

// The stand-in folder window ShowInFolderCatcher registers. It and the interfaces it answers are public
// because .NET only hands out COM interfaces of types that are visible from COM.
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[ComDefaultInterface(typeof(IWebBrowserApp))]
public sealed class ShowInFolderStandIn : IWebBrowserApp, IShellServiceProvider, IShellViewFace, ICustomQueryInterface
{
    private ShowInFolderCatcher? _owner;
    private readonly IntPtr _folderPidl;
    private readonly IntPtr _hwnd;

    internal ShowInFolderStandIn(ShowInFolderCatcher owner, IntPtr folderPidl, IntPtr hwnd)
    {
        _owner = owner;
        _folderPidl = folderPidl;
        _hwnd = hwnd;
    }

    // After this, late calls do nothing (the folder's ID list is freed).
    internal void Forget() => _owner = null;

    // IWebBrowser (the older ID of the same interface) is answered with IWebBrowserApp, which starts with it.
    public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr ppv)
    {
        if (iid == ShowInFolderCatcher.IID_IWebBrowser)
        {
            ppv = Marshal.GetComInterfaceForObject(this, typeof(IWebBrowserApp), CustomQueryInterfaceMode.Ignore);
            return CustomQueryInterfaceResult.Handled;
        }
        ppv = IntPtr.Zero;
        return CustomQueryInterfaceResult.NotHandled;
    }

    // IServiceProvider: every service is this object (as Files does); Windows asks for its view here.
    int IShellServiceProvider.QueryService(ref Guid service, ref Guid riid, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (riid == ShowInFolderCatcher.IID_IUnknown)
            result = Marshal.GetIUnknownForObject(this);
        else if (riid == typeof(IShellViewFace).GUID || riid == ShowInFolderCatcher.IID_IOleWindow)
            result = Marshal.GetComInterfaceForObject(this, typeof(IShellViewFace));
        else if (riid == typeof(IShellServiceProvider).GUID)
            result = Marshal.GetComInterfaceForObject(this, typeof(IShellServiceProvider));
        else if (riid == typeof(IWebBrowserApp).GUID || riid == ShowInFolderCatcher.IID_IWebBrowser || riid == ShowInFolderCatcher.IID_IDispatch)
            result = Marshal.GetComInterfaceForObject(this, typeof(IWebBrowserApp), CustomQueryInterfaceMode.Ignore);
        return result == IntPtr.Zero ? ShowInFolderCatcher.E_NOINTERFACE : ShowInFolderCatcher.S_OK;
    }

    // ---- IShellView: the one that matters is SelectItem.

    int IShellViewFace.GetWindow(out IntPtr hwnd) { hwnd = _hwnd; return _hwnd == IntPtr.Zero ? ShowInFolderCatcher.E_FAIL : ShowInFolderCatcher.S_OK; }
    int IShellViewFace.ContextSensitiveHelp(int enterMode) => ShowInFolderCatcher.E_NOTIMPL;
    int IShellViewFace.TranslateAccelerator(IntPtr message) => ShowInFolderCatcher.S_FALSE;
    int IShellViewFace.EnableModeless(int enable) => ShowInFolderCatcher.S_OK;
    int IShellViewFace.UIActivate(uint state) => ShowInFolderCatcher.S_OK;
    int IShellViewFace.Refresh() => ShowInFolderCatcher.S_OK;
    int IShellViewFace.CreateViewWindow(IntPtr previous, IntPtr settings, IntPtr browser, IntPtr rect, out IntPtr hwnd) { hwnd = IntPtr.Zero; return ShowInFolderCatcher.E_NOTIMPL; }
    int IShellViewFace.DestroyViewWindow() => ShowInFolderCatcher.S_OK;
    int IShellViewFace.GetCurrentInfo(IntPtr settings) { if (settings != IntPtr.Zero) Marshal.WriteInt64(settings, 0); return ShowInFolderCatcher.S_OK; }
    int IShellViewFace.AddPropertySheetPages(uint reserved, IntPtr add, IntPtr lParam) => ShowInFolderCatcher.E_NOTIMPL;
    int IShellViewFace.SaveViewState() => ShowInFolderCatcher.S_OK;
    int IShellViewFace.GetItemObject(uint item, ref Guid riid, out IntPtr result) { result = IntPtr.Zero; return ShowInFolderCatcher.E_NOINTERFACE; }

    int IShellViewFace.SelectItem(IntPtr childPidl, uint flags)
    {
        // Windows also calls this to clear the selection first; only "select / focus / show" names the item.
        if (childPidl == IntPtr.Zero || (flags & (ShowInFolderCatcher.SVSI_SELECT | ShowInFolderCatcher.SVSI_ENSUREVISIBLE | ShowInFolderCatcher.SVSI_FOCUSED)) == 0 || _owner is not { } owner)
            return ShowInFolderCatcher.S_OK;

        var full = ShowInFolderCatcher.ILCombine(_folderPidl, childPidl);
        if (full == IntPtr.Zero)
            return ShowInFolderCatcher.S_OK;
        try
        {
            if (ShowInFolderCatcher.SHGetNameFromIDList(full, ShowInFolderCatcher.SIGDN_FILESYSPATH, out var name) == 0 && name != IntPtr.Zero)
            {
                var path = Marshal.PtrToStringUni(name);
                Marshal.FreeCoTaskMem(name);
                if (!string.IsNullOrEmpty(path) && System.IO.Path.IsPathFullyQualified(path))
                    owner.Deliver(path);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(full); // ILFree
        }
        return ShowInFolderCatcher.S_OK;
    }

    // ---- IWebBrowserApp: a folder window that is hidden, not busy, and closes when asked.

    public void GoBack() => throw new NotImplementedException();
    public void GoForward() => throw new NotImplementedException();
    public void GoHome() => throw new NotImplementedException();
    public void GoSearch() => throw new NotImplementedException();
    public void Navigate(string url, ref object flags, ref object targetFrameName, ref object postData, ref object headers) => throw new NotImplementedException();
    void IWebBrowserApp.Refresh() { }
    public void Refresh2(ref object level) { }
    public void Stop() { }
    public object Application => this;
    public object Parent => this;
    public object Container => this;
    public object Document => this;
    public bool TopLevelContainer => true;
    public string Type => "Clearspace";
    public int Left { get => 0; set { } }
    public int Top { get => 0; set { } }
    public int Width { get => 0; set { } }
    public int Height { get => 0; set { } }
    public string LocationName => string.Empty;
    public string LocationURL => string.Empty;
    public bool Busy => false;
    public void Quit() => _owner?.Stop();
    public void ClientToWindow(ref int x, ref int y) { }
    public void PutProperty(string property, object value) => throw new NotImplementedException();
    public object GetProperty(string property) => throw new NotImplementedException();
    public string Name => "Clearspace";
    public IntPtr HWND => _hwnd;
    public string FullName => Environment.ProcessPath ?? string.Empty;
    public string Path => System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty;
    public bool Visible { get => false; set { } }
    public bool StatusBar { get => false; set { } }
    public string StatusText { get => string.Empty; set { } }
    public int ToolBar { get => 0; set { } }
    public bool MenuBar { get => false; set { } }
    public bool FullScreen { get => false; set { } }
}

[ComVisible(true), ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellServiceProvider
{
    [PreserveSig] int QueryService(ref Guid service, ref Guid riid, out IntPtr result);
}

// IShellView, with the two IOleWindow methods it starts with.
[ComVisible(true), ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellViewFace
{
    [PreserveSig] int GetWindow(out IntPtr hwnd);
    [PreserveSig] int ContextSensitiveHelp(int enterMode);
    [PreserveSig] int TranslateAccelerator(IntPtr message);
    [PreserveSig] int EnableModeless(int enable);
    [PreserveSig] int UIActivate(uint state);
    [PreserveSig] int Refresh();
    [PreserveSig] int CreateViewWindow(IntPtr previous, IntPtr settings, IntPtr browser, IntPtr rect, out IntPtr hwnd);
    [PreserveSig] int DestroyViewWindow();
    [PreserveSig] int GetCurrentInfo(IntPtr settings);
    [PreserveSig] int AddPropertySheetPages(uint reserved, IntPtr add, IntPtr lParam);
    [PreserveSig] int SaveViewState();
    [PreserveSig] int SelectItem(IntPtr childPidl, uint flags);
    [PreserveSig] int GetItemObject(uint item, ref Guid riid, out IntPtr result);
}

// IWebBrowserApp, with the IWebBrowser members it starts with, in the order of exdisp.idl.
[ComVisible(true), ComImport, Guid("0002DF05-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IWebBrowserApp
{
    void GoBack();
    void GoForward();
    void GoHome();
    void GoSearch();
    void Navigate([MarshalAs(UnmanagedType.BStr)] string url, ref object flags, ref object targetFrameName, ref object postData, ref object headers);
    void Refresh();
    void Refresh2(ref object level);
    void Stop();
    object Application { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    object Parent { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    object Container { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    object Document { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    bool TopLevelContainer { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    string Type { [return: MarshalAs(UnmanagedType.BStr)] get; }
    int Left { get; set; }
    int Top { get; set; }
    int Width { get; set; }
    int Height { get; set; }
    string LocationName { [return: MarshalAs(UnmanagedType.BStr)] get; }
    string LocationURL { [return: MarshalAs(UnmanagedType.BStr)] get; }
    bool Busy { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    void Quit();
    void ClientToWindow(ref int x, ref int y);
    void PutProperty([MarshalAs(UnmanagedType.BStr)] string property, object value);
    object GetProperty([MarshalAs(UnmanagedType.BStr)] string property);
    string Name { [return: MarshalAs(UnmanagedType.BStr)] get; }
    IntPtr HWND { get; }
    string FullName { [return: MarshalAs(UnmanagedType.BStr)] get; }
    string Path { [return: MarshalAs(UnmanagedType.BStr)] get; }
    bool Visible { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
    bool StatusBar { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
    string StatusText { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
    int ToolBar { get; set; }
    bool MenuBar { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
    bool FullScreen { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
}
