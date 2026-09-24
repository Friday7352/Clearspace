using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Clearspace.Services;

namespace Clearspace.Controls;

// NEW (round 64): experimental. The folder you are in as a city. Every item inside it is a building on
// the map's own squarified plot - folders are towers, files are low houses, the smallest items share one
// lot - and a tower is made of its contents: each floor is one of its subfolders, stacked largest at the
// bottom over a lobby that holds the files loose in the folder, with the folders too small for a floor of
// their own sharing the top one. Floor heights are the true shares, so the outside of a tower is honest.
//
// Clicking a tower steps inside it: the floors pull apart (an exploded view), the rest of the street fades
// to glass, and one floor slides out toward you like a drawer with its contents laid out on it - its
// subfolders as walled rooms with a doorway, its files as crates coloured by type. The elevator (the
// panel on the right, the arrow keys, or clicking a floor) moves between floors; it never goes deeper.
// Going deeper is a door: double-clicking a room (or a floor, or Enter) makes that folder the tower you
// are in, and the street around it becomes its parent - so every door is an ordinary navigation, and
// Back, Up, breadcrumbs and the list follow along. Esc or Backspace steps back out onto the street.
//
// As in the 3D blocks view, everything that reads the snapshot for the whole street runs off the UI
// thread and is handed over as one frozen mesh coloured through a palette texture. Only the tower you
// are in is split into separate models (one per floor, a dozen or so), so exploding it, sliding a drawer
// and riding the elevator are just transforms eased every frame - nothing is rebuilt while it moves.
public sealed class DiskCityView : Grid
{
    // ------------------------------------------------------------------ tuning
    private const int BuildingLimit = 120;       // largest items in the folder with a building of their own; the rest share a lot
    private const int MaxFloors = 12;            // subfolders with a floor of their own; the rest share the top floor
    private const double MinFloorShare = .02;    // ...and only when they are at least this share of the tower
    private const int ContentLimit = 150;        // rooms and crates on one floor; the rest are one pallet
    private const double Width3D = 1.6, Depth3D = 1.0;
    private const double Tallest = .6, Shortest = .014, HouseScale = .45;
    private const double Street = .012;          // the gap between buildings
    private const double GhostOpacity = .14, GhostFloorOpacity = .26;
    private const double Ease = 7;               // how quickly moving things settle (per second)

    public event Action<int>? FolderOpened;
    public event Action<int>? ItemSelected;
    public event Action? InsideChanged;          // stepping in or out, so Back and Esc know what they will do

    /// <summary>Inside a tower (and not already on the way out): Back and Esc step out rather than navigate.</summary>
    internal bool IsInside => _inside >= 0 && !_leaving;

    // ------------------------------------------------------------------ model

    private enum FloorKind { Folder, Lobby, Misc }

    // Id is the subfolder for a Folder floor, and the tower's own folder for the lobby and the shared floor.
    private sealed record Floor(int Id, FloorKind Kind, string Name, long Bytes, long Files, uint Color, int[]? Members);

    private sealed record Building(int Id, string Name, bool IsFolder, long Bytes, long Files,
        double X, double Z, double W, double D, double H, uint Color, Floor[] Floors)
    {
        public double Side => Math.Max(W, D);
    }

    private readonly record struct Box(double X0, double Y0, double Z0, double X1, double Y1, double Z1, uint Color);

    // Something on the floor that is out: a room (a folder), a crate (a file) or the pallet of smaller items.
    private readonly record struct Target(int Id, bool IsFolder, string Name, long Bytes, long Files, Box Hit);

    private sealed record CityPlan(DiskUsageSnapshot Snapshot, int Folder, string Path, long Total, Building[] Buildings)
    {
        public MeshGeometry3D? Mesh { get; init; }
        public Material? Solid { get; init; }
        public Material? Ghost { get; init; }
    }

    // One floor of the tower you are in. Its mesh is a unit-tall box on the tower's footprint; the scale
    // makes it as tall as it is now and the translation lifts it and slides it out.
    private sealed class FloorModel
    {
        public Floor Floor = null!;
        public GeometryModel3D Model = null!;
        public readonly ScaleTransform3D Scale = new(1, 1, 1);
        public readonly TranslateTransform3D Move = new();
        public Material Solid = null!, Ghost = null!;
        public double ExteriorY, ExteriorH, ExplodedY, ExplodedH;
        public double Y, H, Dx, Dz;          // where it is now
        public double ToY, ToH, ToDx, ToDz;  // where it is going
    }

    private sealed class FloatingLabel
    {
        public TextBlock Text = null!;
        public Func<Point3D?> Anchor = null!;
        public bool Beside;                  // left-aligned beside its anchor rather than centred above it
    }

    private enum HitKind { None, Building, Floor, Content }

    // ------------------------------------------------------------------ state

    private readonly Viewport3D _viewport = new() { ClipToBounds = true };
    private readonly PerspectiveCamera _camera = new() { FieldOfView = 42, NearPlaneDistance = .002, FarPlaneDistance = 50 };
    private readonly Model3DGroup _scene = new();
    private readonly GeometryModel3D _ground = Ground();
    private readonly GeometryModel3D _cityModel = new();
    private readonly ScaleTransform3D _cityGrow = new(1, 1, 1);
    private readonly GeometryModel3D _contentModel = new();
    private readonly ScaleTransform3D _contentGrow = new(1, 1, 1);
    private readonly TranslateTransform3D _contentMove = new();
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Border _card = new();
    private readonly TextBlock _cardTitle = new() { FontWeight = FontWeights.SemiBold, FontSize = 13, MaxWidth = 380, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _cardDetail = new() { FontSize = 11.5, MaxWidth = 380, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { FontSize = 11, Margin = new Thickness(12, 10, 12, 0) };
    private readonly Border _elevator = new();
    private readonly TextBlock _elevatorTitle = new() { FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _elevatorDetail = new() { FontSize = 11, Margin = new Thickness(0, 2, 0, 8) };
    private readonly StackPanel _elevatorRows = new();
    private readonly List<FloatingLabel> _labels = [];
    private readonly List<FloatingLabel> _floorLabels = [];

    private (string Path, long Bytes, long Files)? _requested;
    private DiskUsageSnapshot? _latest;
    private CityPlan? _shown;
    private CancellationTokenSource? _work;
    private string? _pendingEnter;           // a door was taken: the tower to step into once its street is shown
    private string? _pendingFloor;

    // Inside a tower.
    private int _inside = -1;                // index into _shown.Buildings
    private int _selected = -1;              // index into _floors
    private bool _leaving;
    private readonly List<FloorModel> _floors = [];
    private Target[] _contents = [];
    private Material? _streetSolid, _streetGhost;   // the street without the tower you are in, solid and as glass
    private (Point3D Target, double Distance, double Pitch) _streetCamera;
    private double _enteredAt = double.NegativeInfinity;

    // Animation: everything eases toward where it is going, one frame at a time.
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastFrame;
    private bool _animating;
    private double _cityGrowNow = 1, _contentsGrowNow = 1;

    // Orbit camera about a target.
    private double _yaw = -32, _pitch = 48, _distance = 2.3;
    private Point3D _target = new(Width3D / 2, 0, Depth3D / 2);
    private (Point3D Target, double Distance, double Pitch)? _flyTo;
    private Point _press;
    private bool _dragging, _panning, _moved;
    private (HitKind Kind, int Index) _hover = (HitKind.None, -1);

    private static readonly Brush Ink = Frozen(Color.FromRgb(0xEC, 0xE9, 0xE3));
    private static readonly Brush InkMuted = Frozen(Color.FromRgb(0x9C, 0x96, 0x8D));
    private static readonly Brush RowSelected = Frozen(Color.FromArgb(0x40, 0xEC, 0xE9, 0xE3));
    private static readonly Color Background3D = Color.FromRgb(0x1A, 0x19, 0x17);
    private static readonly uint[] Palette = DiskUsagePalette.Branches.Select(hex => Pack((Color)ColorConverter.ConvertFromString(hex))).ToArray();
    private static readonly uint Group = Pack((Color)ColorConverter.ConvertFromString(DiskUsagePalette.GroupColor));

    public DiskCityView()
    {
        Background = new SolidColorBrush(Background3D);
        ClipToBounds = true;
        Focusable = true;
        _viewport.Camera = _camera;
        _viewport.Children.Add(new ModelVisual3D { Content = Lights() });
        _viewport.Children.Add(new ModelVisual3D { Content = _scene });
        _cityModel.Transform = _cityGrow;
        var contentTransform = new Transform3DGroup();
        contentTransform.Children.Add(_contentGrow);
        contentTransform.Children.Add(_contentMove);
        _contentModel.Transform = contentTransform;
        Children.Add(_viewport);

        _status.Foreground = InkMuted;
        _status.VerticalAlignment = VerticalAlignment.Top;
        _status.IsHitTestVisible = false;
        Children.Add(_status);

        _cardTitle.Foreground = Ink;
        _cardDetail.Foreground = InkMuted;
        _card.Child = new StackPanel { Children = { _cardTitle, _cardDetail } };
        _card.Background = Frozen(Color.FromArgb(0xF4, 0x23, 0x22, 0x20));
        _card.BorderBrush = Frozen(Color.FromRgb(0x3A, 0x37, 0x32));
        _card.BorderThickness = new Thickness(1);
        _card.CornerRadius = new CornerRadius(8);
        _card.Padding = new Thickness(12, 9, 12, 10);
        _card.Visibility = Visibility.Collapsed;
        _overlay.Children.Add(_card);
        Children.Add(_overlay);

        // The elevator: the floors of the tower you are in, top floor first, with their share of it.
        _elevatorTitle.Foreground = Ink;
        _elevatorDetail.Foreground = InkMuted;
        var elevatorHeader = new StackPanel { Children = { _elevatorTitle, _elevatorDetail } };
        DockPanel.SetDock(elevatorHeader, Dock.Top);
        var elevatorHint = new TextBlock
        {
            Text = "↑/↓ ride · double-click or Enter goes through · Esc steps out",
            Foreground = InkMuted, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        };
        DockPanel.SetDock(elevatorHint, Dock.Bottom);
        var elevatorScroll = new ScrollViewer { Content = _elevatorRows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _elevator.Child = new DockPanel { Children = { elevatorHeader, elevatorHint, elevatorScroll } };
        _elevator.Width = 240;
        _elevator.Margin = new Thickness(0, 10, 10, 10);
        _elevator.Padding = new Thickness(10, 10, 10, 10);
        _elevator.HorizontalAlignment = HorizontalAlignment.Right;
        _elevator.VerticalAlignment = VerticalAlignment.Top;
        _elevator.Background = Frozen(Color.FromArgb(0xE8, 0x23, 0x22, 0x20));
        _elevator.BorderBrush = Frozen(Color.FromRgb(0x3A, 0x37, 0x32));
        _elevator.BorderThickness = new Thickness(1);
        _elevator.CornerRadius = new CornerRadius(8);
        _elevator.Visibility = Visibility.Collapsed;
        Children.Add(_elevator);

        ArrangeScene();
        UpdateCamera();
        // Hidden mid-build: forget what was asked for, so showing it again builds it rather than skipping.
        IsVisibleChanged += (_, _) => { if (!IsVisible && _work is not null) { Cancel(); _requested = null; } };
    }

    /// <summary>Shows a folder of a snapshot as a street. Rebuilds only if something that affects it changed.</summary>
    internal void Show(DiskUsageSnapshot snapshot, int folder)
    {
        _latest = snapshot;
        var path = snapshot.PathFor(folder);
        var bytes = snapshot.BytesOf(folder);
        var files = snapshot.FilesOf(folder);
        // Live refreshes replace the snapshot every few seconds while a drive is indexed; the same folder
        // with the same totals looks the same, so it keeps what is on screen - including the tower you are in.
        if (_requested is { } requested && requested.Bytes == bytes && requested.Files == files &&
            string.Equals(requested.Path, path, StringComparison.OrdinalIgnoreCase)) return;
        _requested = (path, bytes, files);
        Cancel();
        var work = _work = new CancellationTokenSource();
        var token = work.Token;
        if (_shown is null) _status.Text = "Building the city…";
        Task.Run(() =>
        {
            var plan = Plan(snapshot, folder, path, token);
            var (mesh, solid, ghost) = BuildMesh(CityBoxes(plan.Buildings, -1), bottoms: false);
            return plan with { Mesh = mesh, Solid = solid, Ghost = ghost };
        }, token).ContinueWith(task => Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_work, work)) return;
            _work = null;
            if (task.Status == TaskStatus.RanToCompletion) Present(task.Result);
            else if (task.Exception is { } failure)
            {
                _requested = null;   // try again on the next Show
                _status.Text = $"The city could not be built: {failure.GetBaseException().Message}";
            }
        }), TaskScheduler.Default);
    }

    /// <summary>Empties the view, with a message - a drive that is waiting for its index.</summary>
    internal void Clear(string? message)
    {
        Cancel();
        _requested = null;
        _pendingEnter = _pendingFloor = null;
        LeaveNow();
        _shown = null;
        _cityModel.Geometry = null;
        ArrangeScene();
        ClearLabels(_labels);
        _card.Visibility = Visibility.Collapsed;
        _status.Text = message ?? "";
    }

    /// <summary>Steps out of the tower you are in: the floors fold back into place and the street returns.</summary>
    internal void Exit()
    {
        if (!IsInside || _shown is null) return;
        _leaving = true;
        foreach (var floor in _floors)
        {
            floor.ToY = floor.ExteriorY;
            floor.ToH = floor.ExteriorH;
            floor.ToDx = floor.ToDz = 0;
            floor.Model.Material = floor.Model.BackMaterial = floor.Solid;
        }
        // The street comes back solid straight away; the tower itself stays as its floors until they land.
        _cityModel.Material = _cityModel.BackMaterial = _streetSolid;
        _contentModel.Geometry = null;
        _contents = [];
        _elevator.Visibility = Visibility.Collapsed;
        _card.Visibility = Visibility.Collapsed;
        _hover = (HitKind.None, -1);
        ClearLabels(_floorLabels);
        _flyTo = _streetCamera;
        ArrangeScene();
        _status.Text = StreetStatus();
        InsideChanged?.Invoke();
        Animate();
    }

    private void Cancel()
    {
        _work?.Cancel();
        _work = null;
    }

    // ------------------------------------------------------------------ layout (thread pool)

    private static CityPlan Plan(DiskUsageSnapshot snapshot, int folder, string path, CancellationToken token)
    {
        var ids = new List<int>();
        snapshot.ChildIds(folder, ids);
        var sorted = ids.Where(id => snapshot.BytesOf(id) > 0).OrderByDescending(snapshot.BytesOf).ToArray();
        var shown = sorted.Take(BuildingLimit).ToArray();
        var rest = sorted.Skip(BuildingLimit).ToArray();
        var restBytes = rest.Sum(snapshot.BytesOf);
        var weights = shown.Select(snapshot.BytesOf).ToList();
        if (restBytes > 0) weights.Add(restBytes);
        var buildings = new List<Building>(weights.Count);
        if (weights.Count == 0) return new CityPlan(snapshot, folder, path, 0, []);
        var maxBytes = Math.Max(1, weights.Max());

        foreach (var tile in SquarifiedTreemap.Layout(weights, Width3D, Depth3D))
        {
            token.ThrowIfCancellationRequested();
            var gap = Math.Min(Street, Math.Min(tile.Width, tile.Height) * .12);
            var w = tile.Width - gap;
            var d = tile.Height - gap;
            if (w <= .002 || d <= .002) continue;
            var x = tile.X + gap / 2;
            var z = tile.Y + gap / 2;
            if (tile.ItemIndex >= shown.Length)
            {
                // The smallest items share one low lot.
                buildings.Add(new Building(-1, $"{rest.Length:N0} smaller items", false, restBytes, rest.Sum(snapshot.FilesOf),
                    x, z, w, d, .008, Group, []));
                continue;
            }
            var id = shown[tile.ItemIndex];
            var bytes = snapshot.BytesOf(id);
            var isFolder = snapshot.IsFolderEntry(id);
            var color = Palette[tile.ItemIndex % Palette.Length];
            var height = Shortest + (Tallest - Shortest) * Math.Sqrt(bytes / (double)maxBytes);
            if (!isFolder) { height *= HouseScale; color = Shade(color, .72); }   // files are houses, not towers
            var floors = isFolder ? Floors(snapshot, id, color, token) : [];
            buildings.Add(new Building(id, new string(snapshot.NameOf(id)), isFolder, bytes, Math.Max(1, snapshot.FilesOf(id)),
                x, z, w, d, height, color, floors));
        }
        return new CityPlan(snapshot, folder, path, snapshot.BytesOf(folder), [.. buildings]);
    }

    // A tower's floors, bottom to top: the lobby (files loose in the folder), its largest subfolders, and
    // one shared floor for the subfolders too small to be a floor of their own.
    private static Floor[] Floors(DiskUsageSnapshot snapshot, int id, uint color, CancellationToken token)
    {
        var ids = new List<int>();
        snapshot.ChildIds(id, ids);
        long looseBytes = 0, looseFiles = 0;
        var folders = new List<int>();
        foreach (var child in ids)
        {
            if (snapshot.BytesOf(child) <= 0) continue;
            if (snapshot.IsFolderEntry(child)) folders.Add(child);
            else { looseBytes += snapshot.BytesOf(child); looseFiles++; }
        }
        token.ThrowIfCancellationRequested();
        folders.Sort((a, b) => snapshot.BytesOf(b).CompareTo(snapshot.BytesOf(a)));
        var total = Math.Max(1, snapshot.BytesOf(id));
        var named = new List<int>();
        var small = new List<int>();
        foreach (var folder in folders)
            if (named.Count < MaxFloors && snapshot.BytesOf(folder) >= total * MinFloorShare) named.Add(folder);
            else small.Add(folder);

        var floors = new List<Floor>(named.Count + 2);
        if (looseBytes > 0) floors.Add(new Floor(id, FloorKind.Lobby, "Lobby · loose files", looseBytes, looseFiles, Shade(color, .62), null));
        for (var i = 0; i < named.Count; i++)
            floors.Add(new Floor(named[i], FloorKind.Folder, new string(snapshot.NameOf(named[i])), snapshot.BytesOf(named[i]),
                snapshot.FilesOf(named[i]), Shade(color, i % 2 == 0 ? .96 : .82), null));
        if (small.Count > 0)
            floors.Add(new Floor(id, FloorKind.Misc, $"{small.Count:N0} smaller folder{(small.Count == 1 ? "" : "s")}",
                small.Sum(snapshot.BytesOf), small.Sum(snapshot.FilesOf), Shade(color, .7), [.. small]));
        return [.. floors];
    }

    // The street as boxes: each tower is its floors with a thin dark ledge between them and a small plant
    // room on the roof; houses and the shared lot are single boxes. `skip` leaves out the tower you are in.
    private static List<Box> CityBoxes(Building[] buildings, int skip)
    {
        var boxes = new List<Box>(buildings.Length * 8);
        for (var i = 0; i < buildings.Length; i++)
        {
            if (i == skip) continue;
            var b = buildings[i];
            if (b.Floors.Length == 0)
            {
                Add(boxes, new Box(b.X, 0, b.Z, b.X + b.W, b.H, b.Z + b.D, b.Color));
                continue;
            }
            var total = Math.Max(1, b.Floors.Sum(floor => floor.Bytes));
            var lip = Math.Min(b.Side * .02, .003);
            var ledge = Shade(b.Color, .5);
            var y = 0d;
            for (var f = 0; f < b.Floors.Length; f++)
            {
                var h = b.H * b.Floors[f].Bytes / (double)total;
                Add(boxes, new Box(b.X, y, b.Z, b.X + b.W, y + h, b.Z + b.D, b.Floors[f].Color));
                var t = Math.Min(.0025, h * .3);
                if (f < b.Floors.Length - 1 && t > .0003)
                    Add(boxes, new Box(b.X - lip, y + h - t, b.Z - lip, b.X + b.W + lip, y + h, b.Z + b.D + lip, ledge));
                y += h;
            }
            Add(boxes, new Box(b.X + b.W * .18, y, b.Z + b.D * .18, b.X + b.W * .42, y + Math.Min(b.Side * .08, .018),
                b.Z + b.D * .42, Shade(b.Color, .58)));
        }
        return boxes;
    }

    private static void Add(List<Box> boxes, Box box)
    {
        if (box.X1 > box.X0 && box.Y1 > box.Y0 && box.Z1 > box.Z0) boxes.Add(box);
    }

    // ------------------------------------------------------------------ the street (UI thread)

    private void Present(CityPlan plan)
    {
        var sameStreet = _shown is not null && string.Equals(_shown.Path, plan.Path, StringComparison.OrdinalIgnoreCase);
        // Step back into the tower you were in (a live refresh of the same street), or the one a door led to.
        var enter = _pendingEnter;
        var floor = _pendingFloor;
        if (enter is null && sameStreet && _inside >= 0 && !_leaving && _shown is { } old)
        {
            enter = old.Snapshot.PathFor(old.Buildings[_inside].Id);
            floor = _selected >= 0 && _selected < _floors.Count ? _floors[_selected].Floor.Name : null;
        }
        var streetCamera = _streetCamera;
        var wasInside = _inside >= 0;
        _pendingEnter = _pendingFloor = null;
        LeaveNow();
        _shown = plan;
        _cityModel.Geometry = plan.Mesh;
        _cityModel.Material = _cityModel.BackMaterial = plan.Solid;
        _card.Visibility = Visibility.Collapsed;
        _hover = (HitKind.None, -1);
        if (!sameStreet)
        {
            // A new street grows out of the ground with the camera reset.
            _cityGrowNow = 0;
            _flyTo = null;
            _yaw = -32;
            _pitch = 48;
            _distance = 2.3;
            _target = new Point3D(Width3D / 2, 0, Depth3D / 2);
            UpdateCamera();
        }
        _status.Text = plan.Buildings.Length == 0 ? "Nothing here takes up any space." : StreetStatus();
        BuildStreetLabels();
        ArrangeScene();
        if (enter is not null)
        {
            var index = Array.FindIndex(plan.Buildings, building => building.IsFolder && building.Id >= 0 &&
                string.Equals(plan.Snapshot.PathFor(building.Id), enter, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                StepInside(index, floor, animate: !sameStreet);
                if (sameStreet && wasInside) _streetCamera = streetCamera;   // a refresh keeps where "out" goes
            }
        }
        Animate();
    }

    private string StreetStatus() => _shown is { } plan
        ? $"3D city (experimental) · {plan.Buildings.Length:N0} buildings · click a tower to step inside"
        : "";

    // ------------------------------------------------------------------ inside a tower

    private void StepInside(int index, string? floorName, bool animate)
    {
        if (_shown is not { } plan || index < 0 || index >= plan.Buildings.Length) return;
        var building = plan.Buildings[index];
        if (!building.IsFolder || building.Floors.Length == 0) return;
        if (_inside >= 0) LeaveNow();                                  // from one tower straight into another
        else _streetCamera = (_target, _distance, _pitch);
        _inside = index;
        _leaving = false;
        _enteredAt = _clock.Elapsed.TotalSeconds;
        _card.Visibility = Visibility.Collapsed;
        _hover = (HitKind.None, -1);

        // The rest of the street turns to glass.
        var (street, streetSolid, streetGhost) = BuildMesh(CityBoxes(plan.Buildings, index), bottoms: false);
        _streetSolid = streetSolid;
        _streetGhost = streetGhost;
        _cityModel.Geometry = street;
        _cityModel.Material = _cityModel.BackMaterial = _streetGhost;

        // The floors, from where they stand in the tower to where they hang in the exploded view: each
        // at least readable, none dominating, with room between them to see the plates.
        var side = building.Side;
        var total = Math.Max(1, building.Floors.Sum(floor => floor.Bytes));
        var gap = side * .3;
        var box = UnitBox(building);
        double exteriorY = 0, explodedY = 0;
        foreach (var floor in building.Floors)
        {
            var exteriorH = building.H * floor.Bytes / (double)total;
            var explodedH = Math.Clamp(exteriorH, side * .05, side * .3);
            var model = new FloorModel
            {
                Floor = floor, ExteriorY = exteriorY, ExteriorH = exteriorH, ExplodedY = explodedY, ExplodedH = explodedH,
                Solid = SolidMaterial(floor.Color, 1), Ghost = SolidMaterial(floor.Color, GhostFloorOpacity),
            };
            model.Model = new GeometryModel3D(box, model.Solid) { BackMaterial = model.Solid };
            var transform = new Transform3DGroup();
            transform.Children.Add(model.Scale);
            transform.Children.Add(model.Move);
            model.Model.Transform = transform;
            model.Y = animate ? exteriorY : explodedY;
            model.H = animate ? exteriorH : explodedH;
            model.ToY = explodedY;
            model.ToH = explodedH;
            Apply(model);
            _floors.Add(model);
            exteriorY += exteriorH;
            explodedY += explodedH + gap;
        }

        BuildElevator(building);
        ClearLabels(_labels);
        BuildFloorLabels(building);
        var start = floorName is null ? -1 : _floors.FindIndex(model => model.Floor.Name == floorName);
        SelectFloor(start >= 0 ? start : LargestFloor(), animate);
        InsideChanged?.Invoke();
        Animate();
    }

    private int LargestFloor()
    {
        var best = 0;
        for (var i = 0; i < _floors.Count; i++)
            if (_floors[i].Floor.Kind == FloorKind.Folder &&
                (_floors[best].Floor.Kind != FloorKind.Folder || _floors[i].Floor.Bytes > _floors[best].Floor.Bytes)) best = i;
        return best;
    }

    // The elevator: one floor out like a drawer, toward the camera and out from under the floor above; the
    // others hang back as glass. Only its own contents are built, and they rise out of its plate.
    private void SelectFloor(int index, bool animate = true)
    {
        if (!IsInside || _shown is not { } plan || _floors.Count == 0) return;
        index = Math.Clamp(index, 0, _floors.Count - 1);
        var building = plan.Buildings[_inside];
        _selected = index;
        var yaw = _yaw * Math.PI / 180;
        var slide = building.Side * .8;
        var dx = Math.Sin(yaw) * slide;
        var dz = Math.Cos(yaw) * slide;
        for (var i = 0; i < _floors.Count; i++)
        {
            var floor = _floors[i];
            var chosen = i == index;
            floor.ToY = floor.ExplodedY;
            floor.ToH = floor.ExplodedH;
            floor.ToDx = chosen ? dx : 0;
            floor.ToDz = chosen ? dz : 0;
            floor.Model.Material = floor.Model.BackMaterial = chosen ? floor.Solid : floor.Ghost;
            if (!animate)
            {
                floor.Y = floor.ToY; floor.H = floor.ToH; floor.Dx = floor.ToDx; floor.Dz = floor.ToDz;
                Apply(floor);
            }
        }
        BuildContents(building, _floors[index].Floor);
        _contentsGrowNow = animate ? 0 : 1;
        _hover = (HitKind.None, -1);   // what is under the pointer has just changed
        PlaceContents();
        var selected = _floors[index];
        var top = selected.ExplodedY + selected.ExplodedH;
        FlyTo((new Point3D(building.X + building.W / 2 + dx, top, building.Z + building.D / 2 + dz),
            Math.Clamp(building.Side * 2.4, .05, 6), 40), animate);
        UpdateElevator();
        ArrangeScene();
        _status.Text = $"Inside {building.Name} · floor {index + 1} of {_floors.Count} · {selected.Floor.Name}";
        Animate();
    }

    // Rooms (subfolders, walled with a doorway), crates (files, coloured by type) and a pallet for the rest,
    // laid out on the floor's plate with the map's squarified layout. Built at plate height 0; the content
    // transform lifts them onto the floor that is out and follows it as it moves.
    private void BuildContents(Building building, Floor floor)
    {
        var snapshot = _shown!.Snapshot;
        var ids = new List<int>();
        if (floor.Kind == FloorKind.Misc && floor.Members is { } members) ids.AddRange(members);
        else
        {
            snapshot.ChildIds(floor.Id, ids);
            if (floor.Kind == FloorKind.Lobby) ids.RemoveAll(snapshot.IsFolderEntry);
        }
        var sorted = ids.Where(id => snapshot.BytesOf(id) > 0).OrderByDescending(snapshot.BytesOf).ToArray();
        var shown = sorted.Take(ContentLimit).ToArray();
        var rest = sorted.Skip(ContentLimit).ToArray();
        var restBytes = rest.Sum(snapshot.BytesOf);
        var weights = shown.Select(snapshot.BytesOf).ToList();
        if (restBytes > 0) weights.Add(restBytes);

        var side = building.Side;
        var boxes = new List<Box>(weights.Count * 6 + 1);
        var targets = new List<Target>(weights.Count);
        var inset = Math.Min(building.W, building.D) * .07;
        var plateTop = side * .004;
        Add(boxes, new Box(building.X + inset * .4, 0, building.Z + inset * .4,
            building.X + building.W - inset * .4, plateTop, building.Z + building.D - inset * .4, Shade(floor.Color, 1.18)));
        double x0 = building.X + inset, z0 = building.Z + inset, w = building.W - inset * 2, d = building.D - inset * 2;
        if (weights.Count > 0 && w > 0 && d > 0)
        {
            var largestFile = shown.Where(id => !snapshot.IsFolderEntry(id)).Select(snapshot.BytesOf).DefaultIfEmpty(1L).Max();
            foreach (var tile in SquarifiedTreemap.Layout(weights, w, d))
            {
                var gap = Math.Min(Math.Min(tile.Width, tile.Height) * .1, side * .012);
                double bx0 = x0 + tile.X + gap / 2, bz0 = z0 + tile.Y + gap / 2;
                double bx1 = x0 + tile.X + tile.Width - gap / 2, bz1 = z0 + tile.Y + tile.Height - gap / 2;
                if (bx1 - bx0 < side * .004 || bz1 - bz0 < side * .004) continue;
                if (tile.ItemIndex >= shown.Length)
                {
                    var pallet = new Box(bx0, plateTop, bz0, bx1, plateTop + side * .012, bz1, Group);
                    Add(boxes, pallet);
                    targets.Add(new Target(-1, false, $"{rest.Length:N0} smaller items", restBytes, rest.Sum(snapshot.FilesOf), pallet));
                    continue;
                }
                var id = shown[tile.ItemIndex];
                var name = new string(snapshot.NameOf(id));
                var bytes = snapshot.BytesOf(id);
                if (snapshot.IsFolderEntry(id))
                {
                    var color = Palette[tile.ItemIndex % Palette.Length];
                    var wallH = side * .045;
                    var top = plateTop + wallH;
                    var t = Math.Clamp(Math.Min(bx1 - bx0, bz1 - bz0) * .08, side * .0015, side * .008);
                    Add(boxes, new Box(bx0, plateTop, bz0, bx1, plateTop + side * .003, bz1, Shade(color, .62)));   // its floor
                    Add(boxes, new Box(bx0, plateTop, bz0, bx0 + t, top, bz1, color));                             // walls
                    Add(boxes, new Box(bx1 - t, plateTop, bz0, bx1, top, bz1, color));
                    Add(boxes, new Box(bx0 + t, plateTop, bz0, bx1 - t, top, bz0 + t, color));
                    var door = Math.Min((bx1 - bx0) * .34, side * .06);                                            // the doorway
                    var middle = (bx0 + bx1) / 2;
                    Add(boxes, new Box(bx0 + t, plateTop, bz1 - t, middle - door / 2, top, bz1, color));
                    Add(boxes, new Box(middle + door / 2, plateTop, bz1 - t, bx1 - t, top, bz1, color));
                    targets.Add(new Target(id, true, name, bytes, snapshot.FilesOf(id), new Box(bx0, plateTop, bz0, bx1, top, bz1, color)));
                }
                else
                {
                    var inner = Math.Min(bx1 - bx0, bz1 - bz0) * .1;
                    var height = side * (.012 + .07 * Math.Sqrt(bytes / (double)Math.Max(1, largestFile)));
                    var crate = new Box(bx0 + inner, plateTop, bz0 + inner, bx1 - inner, plateTop + height, bz1 - inner,
                        KindColor(DiskUsagePalette.KindOf(snapshot.NameOf(id))));
                    Add(boxes, crate);
                    targets.Add(new Target(id, false, name, bytes, 1, crate));
                }
            }
        }
        var (mesh, solid, _) = BuildMesh(boxes, bottoms: false);
        _contentModel.Geometry = mesh;
        _contentModel.Material = _contentModel.BackMaterial = solid;
        _contents = [.. targets];
    }

    // Instantly back on the street, with nothing animating out - a new street, a Clear, or the end of Exit.
    private void LeaveNow()
    {
        var wasInside = _inside >= 0;
        _inside = _selected = -1;
        _leaving = false;
        _floors.Clear();
        _contentModel.Geometry = null;
        _contents = [];
        _streetSolid = _streetGhost = null;
        _elevator.Visibility = Visibility.Collapsed;
        ClearLabels(_floorLabels);
        if (_shown is { } plan)
        {
            _cityModel.Geometry = plan.Mesh;
            _cityModel.Material = _cityModel.BackMaterial = plan.Solid;
        }
        ArrangeScene();
        if (!wasInside) return;
        BuildStreetLabels();
        _status.Text = StreetStatus();
        InsideChanged?.Invoke();
    }

    // Draw order matters once things are glass: opaque models first, transparent ones after, or a glass
    // surface drawn early hides what is behind it.
    private void ArrangeScene()
    {
        _scene.Children.Clear();
        _scene.Children.Add(_ground);
        var selected = IsInside && _selected >= 0 && _selected < _floors.Count ? _selected : -1;
        if (selected >= 0) _scene.Children.Add(_floors[selected].Model);
        if (_contentModel.Geometry is not null) _scene.Children.Add(_contentModel);
        if (_leaving && _cityModel.Geometry is not null) _scene.Children.Add(_cityModel);   // solid again on the way out
        for (var i = 0; i < _floors.Count; i++)
            if (i != selected) _scene.Children.Add(_floors[i].Model);
        if (!_leaving && _cityModel.Geometry is not null) _scene.Children.Add(_cityModel);
    }

    // The elevator panel: floors top to bottom, each with its size and a bar for its share of the tower.
    private void BuildElevator(Building building)
    {
        _elevatorTitle.Text = building.Name;
        _elevatorDetail.Text = $"{DiskUsageSnapshot.FormatBytes(building.Bytes)} · {building.Floors.Length} floor{(building.Floors.Length == 1 ? "" : "s")}";
        _elevatorRows.Children.Clear();
        var total = Math.Max(1, building.Floors.Sum(floor => floor.Bytes));
        for (var i = building.Floors.Length - 1; i >= 0; i--)
        {
            var floor = building.Floors[i];
            var index = i;
            var size = new TextBlock
            {
                Text = DiskUsageSnapshot.FormatBytes(floor.Bytes), FontSize = 11, Foreground = InkMuted,
                Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(size, Dock.Right);
            var name = new TextBlock { Text = floor.Name, FontSize = 12, Foreground = Ink, TextTrimming = TextTrimming.CharacterEllipsis };
            var line = new DockPanel { Children = { size, name } };
            var bar = new Border
            {
                Height = 3, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(2, 196 * floor.Bytes / (double)total), Background = Frozen(Unpack(floor.Color)),
                Margin = new Thickness(0, 4, 0, 0),
            };
            var row = new Border
            {
                Child = new StackPanel { Children = { line, bar } }, Padding = new Thickness(8, 5, 8, 6),
                CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand, Background = Brushes.Transparent, Tag = index,
            };
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (index != _selected) SelectFloor(index); };
            _elevatorRows.Children.Add(row);
        }
        _elevator.Visibility = Visibility.Visible;
    }

    private void UpdateElevator()
    {
        foreach (var child in _elevatorRows.Children)
            if (child is Border { Tag: int index } row)
            {
                row.Background = index == _selected ? RowSelected : Brushes.Transparent;
                if (index == _selected) row.BringIntoView();
            }
        for (var i = 0; i < _floorLabels.Count; i++)
        {
            _floorLabels[i].Text.Foreground = i == _selected ? Ink : InkMuted;
            _floorLabels[i].Text.FontWeight = i == _selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // A door: the folder becomes the tower you are in, and the street around it becomes its parent - an
    // ordinary navigation, so the list, breadcrumbs and history follow. On this street already, just step in.
    private void Door(int id)
    {
        if (_shown is not { } plan || id <= 0) return;
        var snapshot = plan.Snapshot;
        var parent = snapshot.Parent(id);
        _enteredAt = _clock.Elapsed.TotalSeconds;
        if (parent == plan.Folder)
        {
            var index = Array.FindIndex(plan.Buildings, building => building.Id == id);
            if (index >= 0) StepInside(index, null, animate: true);
            return;
        }
        if (Resolve(parent, isFolder: true) is not { } street) return;
        _pendingEnter = snapshot.PathFor(id);
        _pendingFloor = null;
        _status.Text = "Going through the door…";
        FolderOpened?.Invoke(street);
    }

    // ------------------------------------------------------------------ animation

    private void Animate()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = _clock.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Stop()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _lastFrame, 0, .1);
        _lastFrame = now;
        if (!IsVisible) { Settle(); return; }
        var k = 1 - Math.Exp(-dt * Ease);

        var moving = Step(ref _cityGrowNow, 1, k);
        _cityGrow.ScaleY = Math.Max(_cityGrowNow, 1e-4);
        var floorsMoving = false;
        foreach (var floor in _floors)
        {
            floorsMoving |= Step(ref floor.Y, floor.ToY, k) | Step(ref floor.H, floor.ToH, k) |
                            Step(ref floor.Dx, floor.ToDx, k) | Step(ref floor.Dz, floor.ToDz, k);
            Apply(floor);
        }
        moving |= floorsMoving;
        if (_contentModel.Geometry is not null) moving |= Step(ref _contentsGrowNow, 1, k);
        PlaceContents();

        if (_flyTo is { } fly)
        {
            _target += (fly.Target - _target) * k;
            _distance += (fly.Distance - _distance) * k;
            _pitch += (fly.Pitch - _pitch) * k;
            if ((fly.Target - _target).Length < 1e-5 && Math.Abs(fly.Distance - _distance) < 1e-5 && Math.Abs(fly.Pitch - _pitch) < .01)
            {
                (_target, _distance, _pitch) = fly;
                _flyTo = null;
            }
            else moving = true;
            UpdateCamera();
        }
        else PlaceLabels();

        if (_leaving && !floorsMoving) LeaveNow();
        if (!moving) Stop();
    }

    // Everything straight to where it was going - the view was hidden mid-move.
    private void Settle()
    {
        _cityGrowNow = _contentsGrowNow = 1;
        _cityGrow.ScaleY = 1;
        foreach (var floor in _floors)
        {
            floor.Y = floor.ToY; floor.H = floor.ToH; floor.Dx = floor.ToDx; floor.Dz = floor.ToDz;
            Apply(floor);
        }
        PlaceContents();
        if (_flyTo is { } fly) { (_target, _distance, _pitch) = fly; _flyTo = null; UpdateCamera(); }
        if (_leaving) LeaveNow();
        Stop();
    }

    private static bool Step(ref double value, double target, double k)
    {
        var difference = target - value;
        if (Math.Abs(difference) < 1e-6) { value = target; return false; }
        value += difference * k;
        return true;
    }

    private static void Apply(FloorModel floor)
    {
        floor.Scale.ScaleY = Math.Max(floor.H, 1e-6);
        floor.Move.OffsetX = floor.Dx;
        floor.Move.OffsetY = floor.Y;
        floor.Move.OffsetZ = floor.Dz;
    }

    private void PlaceContents()
    {
        _contentGrow.ScaleY = Math.Max(_contentsGrowNow, 1e-4);
        if (_selected < 0 || _selected >= _floors.Count) return;
        var floor = _floors[_selected];
        _contentMove.OffsetX = floor.Dx;
        _contentMove.OffsetY = floor.Y + floor.H;
        _contentMove.OffsetZ = floor.Dz;
    }

    private void FlyTo((Point3D Target, double Distance, double Pitch) to, bool animate)
    {
        if (animate) { _flyTo = to; return; }
        _flyTo = null;
        (_target, _distance, _pitch) = to;
        UpdateCamera();
    }

    // ------------------------------------------------------------------ mesh

    // Boxes to one mesh, coloured through a palette texture with one texel per distinct colour, plus a
    // glass version of the same material. Safe off the UI thread: everything is frozen.
    private static (MeshGeometry3D Mesh, Material Solid, Material Ghost) BuildMesh(IReadOnlyList<Box> boxes, bool bottoms)
    {
        var colors = new Dictionary<uint, int>();
        foreach (var box in boxes) colors.TryAdd(box.Color, colors.Count);
        var width = Math.Max(1, colors.Count);
        var pixels = new int[width];
        foreach (var (color, index) in colors) pixels[index] = unchecked((int)(0xFF000000 | color));
        var texture = BitmapSource.Create(width, 1, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        texture.Freeze();

        var positions = new Point3DCollection(boxes.Count * 24);
        var normals = new Vector3DCollection(boxes.Count * 24);
        var uvs = new PointCollection(boxes.Count * 24);
        var triangles = new Int32Collection(boxes.Count * 36);
        foreach (var box in boxes)
            AddBox(positions, normals, uvs, triangles, box, (colors[box.Color] + .5) / width, bottoms);
        var mesh = new MeshGeometry3D { Positions = positions, Normals = normals, TextureCoordinates = uvs, TriangleIndices = triangles };
        mesh.Freeze();
        return (mesh, Make(1), Make(GhostOpacity));

        Material Make(double opacity)
        {
            var brush = new ImageBrush(texture)
            {
                ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill, Opacity = opacity,
            };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
            brush.Freeze();
            var material = new DiffuseMaterial(brush);
            material.Freeze();
            return material;
        }
    }

    // A floor of the tower you are in: the tower's footprint, one unit tall, bottom included (floors hang
    // in the air, so their undersides show).
    private static MeshGeometry3D UnitBox(Building building)
    {
        var positions = new Point3DCollection(24);
        var normals = new Vector3DCollection(24);
        var triangles = new Int32Collection(36);
        AddBox(positions, normals, null, triangles,
            new Box(building.X, 0, building.Z, building.X + building.W, 1, building.Z + building.D, 0), 0, bottom: true);
        var mesh = new MeshGeometry3D { Positions = positions, Normals = normals, TriangleIndices = triangles };
        mesh.Freeze();
        return mesh;
    }

    private static void AddBox(Point3DCollection positions, Vector3DCollection normals, PointCollection? uvs,
        Int32Collection triangles, Box box, double u, bool bottom)
    {
        double x0 = box.X0, x1 = box.X1, y0 = box.Y0, y1 = box.Y1, z0 = box.Z0, z1 = box.Z1;
        Face(new(x0, y1, z0), new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0), new(0, 1, 0));    // top
        Face(new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1), new(0, 0, 1));    // front
        Face(new(x1, y0, z0), new(x0, y0, z0), new(x0, y1, z0), new(x1, y1, z0), new(0, 0, -1));   // back
        Face(new(x1, y0, z1), new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1), new(1, 0, 0));    // right
        Face(new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0), new(-1, 0, 0));   // left
        if (bottom) Face(new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1), new(x0, y0, z1), new(0, -1, 0));

        void Face(Point3D a, Point3D b, Point3D c, Point3D d, Vector3D normal)
        {
            var start = positions.Count;
            positions.Add(a); positions.Add(b); positions.Add(c); positions.Add(d);
            for (var i = 0; i < 4; i++)
            {
                normals.Add(normal);
                uvs?.Add(new Point(u, .5));
            }
            triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
            triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 3);
        }
    }

    private static Material SolidMaterial(uint color, double opacity)
    {
        var brush = new SolidColorBrush(Unpack(color)) { Opacity = opacity };
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }

    private static Model3DGroup Lights()
    {
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(0x62, 0x62, 0x62)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0xC4, 0xC2, 0xBC), new Vector3D(-.45, -1, -.3)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x38, 0x3A, 0x40), new Vector3D(.6, -.25, .8)));
        lights.Freeze();
        return lights;
    }

    private static GeometryModel3D Ground()
    {
        const double margin = .06;
        var mesh = new MeshGeometry3D
        {
            Positions = [new(-margin, -.001, -margin), new(-margin, -.001, Depth3D + margin),
                new(Width3D + margin, -.001, Depth3D + margin), new(Width3D + margin, -.001, -margin)],
            TriangleIndices = [0, 1, 2, 0, 2, 3],
            Normals = [new(0, 1, 0), new(0, 1, 0), new(0, 1, 0), new(0, 1, 0)],
        };
        var material = new DiffuseMaterial(Frozen(Color.FromRgb(0x2A, 0x28, 0x25)));
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    // ------------------------------------------------------------------ labels

    // Names on the roofs of the largest buildings on the street.
    private void BuildStreetLabels()
    {
        ClearLabels(_labels);
        if (_shown is not { } plan) return;
        foreach (var building in plan.Buildings.Where(building => building.Id >= 0).OrderByDescending(building => building.W * building.D).Take(14))
        {
            var text = MakeLabel(building.Name, 12, Ink, FontWeights.SemiBold);
            _labels.Add(new FloatingLabel
            {
                Text = text,
                Anchor = () => new Point3D(building.X + building.W / 2, building.H * _cityGrowNow + .01, building.Z + building.D / 2),
            });
            _overlay.Children.Insert(0, text);
        }
        PlaceLabels();
    }

    // Names beside each floor of the tower you are in, on whichever side of it is the camera's right.
    private void BuildFloorLabels(Building building)
    {
        ClearLabels(_floorLabels);
        foreach (var floor in _floors)
        {
            var text = MakeLabel($"{floor.Floor.Name} · {DiskUsageSnapshot.FormatBytes(floor.Floor.Bytes)}", 11.5, InkMuted, FontWeights.Normal);
            _floorLabels.Add(new FloatingLabel
            {
                Text = text,
                Beside = true,
                Anchor = () =>
                {
                    var yaw = _yaw * Math.PI / 180;
                    var right = new Vector3D(Math.Cos(yaw), 0, -Math.Sin(yaw));
                    var reach = Math.Abs(right.X) * building.W / 2 + Math.Abs(right.Z) * building.D / 2 + building.Side * .06;
                    return new Point3D(building.X + building.W / 2 + floor.Dx + right.X * reach, floor.Y + floor.H / 2,
                        building.Z + building.D / 2 + floor.Dz + right.Z * reach);
                },
            });
            _overlay.Children.Insert(0, text);
        }
        PlaceLabels();
    }

    private static TextBlock MakeLabel(string text, double size, Brush foreground, FontWeight weight) => new()
    {
        Text = text, Foreground = foreground, FontSize = size, FontWeight = weight,
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = .9 },
    };

    private void ClearLabels(List<FloatingLabel> labels)
    {
        foreach (var label in labels) _overlay.Children.Remove(label.Text);
        labels.Clear();
    }

    private void PlaceLabels()
    {
        Place(_labels);
        Place(_floorLabels);

        void Place(List<FloatingLabel> labels)
        {
            foreach (var label in labels)
            {
                if (label.Anchor() is { } anchor && Project(anchor) is { } point)
                {
                    label.Text.Visibility = Visibility.Visible;
                    label.Text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var size = label.Text.DesiredSize;
                    Canvas.SetLeft(label.Text, label.Beside ? point.X + 4 : point.X - size.Width / 2);
                    Canvas.SetTop(label.Text, label.Beside ? point.Y - size.Height / 2 : point.Y - size.Height - 4);
                }
                else label.Text.Visibility = Visibility.Collapsed;
            }
        }
    }

    // World point to control pixels through the perspective camera; null when behind the camera.
    private Point? Project(Point3D world)
    {
        if (ActualWidth < 1 || ActualHeight < 1) return null;
        var forward = _camera.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, _camera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        var relative = world - _camera.Position;
        var depth = Vector3D.DotProduct(relative, forward);
        if (depth <= _camera.NearPlaneDistance) return null;
        var scale = 1 / Math.Tan(_camera.FieldOfView * Math.PI / 360);   // horizontal field of view in WPF
        var x = Vector3D.DotProduct(relative, right) / depth * scale;
        var y = Vector3D.DotProduct(relative, up) / depth * scale * (ActualWidth / ActualHeight);
        return new Point((x + 1) / 2 * ActualWidth, (1 - y) / 2 * ActualHeight);
    }

    // ------------------------------------------------------------------ camera and input

    private void UpdateCamera()
    {
        var yaw = _yaw * Math.PI / 180;
        var pitch = _pitch * Math.PI / 180;
        var offset = new Vector3D(Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch), Math.Cos(pitch) * Math.Cos(yaw)) * _distance;
        _camera.Position = _target + offset;
        _camera.LookDirection = -offset;
        _camera.UpDirection = new Vector3D(0, 1, 0);
        PlaceLabels();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_elevator.IsMouseOver) return;
        Focus();
        if (e.ClickCount == 2)
        {
            // The first click already did its single-click work; a double-click right after stepping in
            // is the same click landing on the floors that just opened, not a door.
            if (_clock.Elapsed.TotalSeconds - _enteredAt > .6) Click(e.GetPosition(this), twice: true);
            e.Handled = true;
            return;
        }
        _press = e.GetPosition(this);
        _dragging = true;
        _panning = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _moved = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (_elevator.IsMouseOver) return;
        _press = e.GetPosition(this);
        _dragging = _panning = true;
        _moved = true;   // a right press never clicks
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = _panning = false;   // Alt-Tab mid-drag must not leave the camera following the pointer
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        var click = e.ChangedButton == MouseButton.Left && !_moved;
        _dragging = _panning = false;
        ReleaseMouseCapture();
        if (click) Click(e.GetPosition(this), twice: false);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (_dragging)
        {
            var delta = point - _press;
            if (!_moved)
            {
                if (delta.Length <= 3) return;   // still a click
                _moved = true;
            }
            _press = point;
            _flyTo = null;   // taking the camera stops any flight
            if (_panning)
            {
                // Move the target across the ground, in the camera's own left-right and forward directions.
                var yaw = _yaw * Math.PI / 180;
                var right = new Vector3D(Math.Cos(yaw), 0, -Math.Sin(yaw));
                var forward = new Vector3D(-Math.Sin(yaw), 0, -Math.Cos(yaw));
                var speed = _distance * .0016;
                _target += (-right * delta.X + forward * delta.Y) * speed;
            }
            else
            {
                _yaw -= delta.X * .35;
                _pitch = Math.Clamp(_pitch + delta.Y * .3, 8, 88);
            }
            UpdateCamera();
            _card.Visibility = Visibility.Collapsed;
            return;
        }
        if (_elevator.IsMouseOver) { _card.Visibility = Visibility.Collapsed; _hover = (HitKind.None, -1); return; }
        Hover(point);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _card.Visibility = Visibility.Collapsed;
        _hover = (HitKind.None, -1);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled || _elevator.IsMouseOver) return;
        _flyTo = null;
        _distance = Math.Clamp(_distance * Math.Pow(.88, e.Delta / 120d), .02, 8);
        UpdateCamera();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !IsInside) return;
        switch (e.Key)
        {
            case Key.Up:
            case Key.PageUp:
                SelectFloor(Math.Min(_selected + 1, _floors.Count - 1));
                e.Handled = true;
                break;
            case Key.Down:
            case Key.PageDown:
                SelectFloor(Math.Max(_selected - 1, 0));
                e.Handled = true;
                break;
            case Key.Enter when _selected >= 0 && _floors[_selected].Floor.Kind == FloorKind.Folder:
                Door(_floors[_selected].Floor.Id);
                e.Handled = true;
                break;
            case Key.Back:
                Exit();
                e.Handled = true;
                break;
        }
    }

    private void Click(Point point, bool twice)
    {
        if (_shown is not { } plan) return;
        var (kind, index) = HitTest(point);
        switch (kind)
        {
            case HitKind.Building:
                var building = plan.Buildings[index];
                if (building.IsFolder && building.Floors.Length > 0) StepInside(index, null, animate: true);
                else if (building.Id >= 0 && !building.IsFolder && Resolve(building.Id, isFolder: false) is { } file) ItemSelected?.Invoke(file);
                break;
            case HitKind.Floor:
                var floor = _floors[index].Floor;
                if (twice && floor.Kind == FloorKind.Folder) Door(floor.Id);
                else if (index != _selected) SelectFloor(index);
                break;
            case HitKind.Content:
                var target = _contents[index];
                if (!twice || target.Id < 0) break;
                if (target.IsFolder) Door(target.Id);
                else if (Resolve(target.Id, isFolder: false) is { } item) ItemSelected?.Invoke(item);
                break;
        }
    }

    // What is under a point. Inside a tower its contents and floors come first; the glass street behind
    // them is only reached where they are not.
    private (HitKind Kind, int Index) HitTest(Point point)
    {
        (HitKind Kind, int Index) found = (HitKind.None, -1);
        if (_shown is not { } plan || !Ray(point, out var origin, out var ray)) return found;
        var nearest = double.MaxValue;
        if (IsInside)
        {
            var building = plan.Buildings[_inside];
            if (_selected >= 0 && _selected < _floors.Count && _contentsGrowNow > .5)
            {
                var floor = _floors[_selected];
                var top = floor.Y + floor.H;
                for (var i = 0; i < _contents.Length; i++)
                {
                    var box = _contents[i].Hit;
                    if (RayEnters(origin, ray, box.X0 + floor.Dx, box.X1 + floor.Dx, box.Y0 + top, box.Y1 + top,
                        box.Z0 + floor.Dz, box.Z1 + floor.Dz) is { } t && t < nearest) { nearest = t; found = (HitKind.Content, i); }
                }
            }
            for (var i = 0; i < _floors.Count; i++)
            {
                var floor = _floors[i];
                if (RayEnters(origin, ray, building.X + floor.Dx, building.X + building.W + floor.Dx, floor.Y, floor.Y + floor.H,
                    building.Z + floor.Dz, building.Z + building.D + floor.Dz) is { } t && t < nearest) { nearest = t; found = (HitKind.Floor, i); }
            }
            if (found.Kind != HitKind.None) return found;
        }
        var grown = _cityGrowNow;
        for (var i = 0; i < plan.Buildings.Length; i++)
        {
            if (i == _inside) continue;
            var building = plan.Buildings[i];
            if (RayEnters(origin, ray, building.X, building.X + building.W, 0, building.H * grown, building.Z, building.Z + building.D) is { } t &&
                t < nearest) { nearest = t; found = (HitKind.Building, i); }
        }
        return found;
    }

    // A ray from the camera through a point of the control (Project, inverted).
    private bool Ray(Point point, out Point3D origin, out Vector3D ray)
    {
        origin = _camera.Position;
        ray = default;
        if (ActualWidth < 1 || ActualHeight < 1) return false;
        var forward = _camera.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, _camera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        var scale = 1 / Math.Tan(_camera.FieldOfView * Math.PI / 360);
        var nx = point.X / ActualWidth * 2 - 1;
        var ny = 1 - point.Y / ActualHeight * 2;
        ray = forward + right * (nx / scale) + up * (ny / (scale * ActualWidth / ActualHeight));
        return true;
    }

    // Where a ray enters a box (slab method), or null if it misses.
    private static double? RayEnters(Point3D o, Vector3D d, double x0, double x1, double y0, double y1, double z0, double z1)
    {
        double near = 0, far = double.MaxValue;
        return Slab(o.X, d.X, x0, x1) && Slab(o.Y, d.Y, y0, y1) && Slab(o.Z, d.Z, z0, z1) ? near : null;

        bool Slab(double start, double step, double low, double high)
        {
            if (Math.Abs(step) < 1e-12) return start >= low && start <= high;
            var a = (low - start) / step;
            var b = (high - start) / step;
            if (a > b) (a, b) = (b, a);
            near = Math.Max(near, a);
            far = Math.Min(far, b);
            return near <= far;
        }
    }

    private void Hover(Point point)
    {
        var hit = HitTest(point);
        if (hit.Kind == HitKind.None || _shown is not { } plan)
        {
            _card.Visibility = Visibility.Collapsed;
            _hover = (HitKind.None, -1);
            return;
        }
        if (hit != _hover)
        {
            _hover = hit;
            (_cardTitle.Text, _cardDetail.Text) = Describe(plan, hit.Kind, hit.Index);
        }
        _card.Visibility = Visibility.Visible;
        _card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var x = point.X + 16;
        var y = point.Y + 18;
        if (x + _card.DesiredSize.Width > ActualWidth - 6) x = point.X - 12 - _card.DesiredSize.Width;
        if (y + _card.DesiredSize.Height > ActualHeight - 6) y = point.Y - 12 - _card.DesiredSize.Height;
        Canvas.SetLeft(_card, Math.Max(6, x));
        Canvas.SetTop(_card, Math.Max(6, y));
    }

    private (string Title, string Detail) Describe(CityPlan plan, HitKind kind, int index)
    {
        var total = Math.Max(1, plan.Total);
        switch (kind)
        {
            case HitKind.Building:
            {
                var b = plan.Buildings[index];
                var line = $"{DiskUsageSnapshot.FormatBytes(b.Bytes)} · {b.Bytes * 100d / total:0.#}% · {Files(b.Files)}";
                if (b.Id < 0) return (b.Name, line);
                if (!b.IsFolder) return (b.Name, $"{line}\nClick to show it in the list");
                var floors = $"{b.Floors.Length} floor{(b.Floors.Length == 1 ? "" : "s")}";
                return (b.Name, $"{line}\n{floors} · click to step {(IsInside ? "into this one instead" : "inside")}");
            }
            case HitKind.Floor:
            {
                var building = plan.Buildings[_inside];
                var f = _floors[index].Floor;
                var share = f.Bytes * 100d / Math.Max(1, building.Bytes);
                var line = $"{DiskUsageSnapshot.FormatBytes(f.Bytes)} · {share:0.#}% of {building.Name} · {Files(f.Files)}";
                var action = f.Kind switch
                {
                    FloorKind.Lobby => "Files loose in the folder itself",
                    FloorKind.Misc => "Folders too small for a floor of their own",
                    _ => "Double-click or Enter to make it the tower you are in",
                };
                return (f.Name, $"{line}\n{(index == _selected ? "" : "Click to ride here · ")}{action}");
            }
            default:
            {
                var t = _contents[index];
                var line = $"{DiskUsageSnapshot.FormatBytes(t.Bytes)} · {Files(t.Files)}";
                if (t.Id < 0) return (t.Name, line);
                if (t.IsFolder) return (t.Name, $"{line}\nA room · double-click to go through the door");
                var type = DiskUsagePalette.KindName(DiskUsagePalette.KindOf(t.Name));
                return (t.Name, $"{DiskUsageSnapshot.FormatBytes(t.Bytes)} · {type.ToLowerInvariant()}\nDouble-click to show it in the list");
            }
        }

        static string Files(long count) => $"{count:N0} file{(count == 1 ? "" : "s")}";
    }

    // The street on screen may come from an older snapshot than the one the view now uses (a live refresh
    // that did not need a rebuild), and a rebuilt index numbers its entries afresh - so ids go by path.
    private int? Resolve(int id, bool isFolder)
    {
        if (_shown?.Snapshot is not { } shown || _latest is not { } latest) return null;
        if (ReferenceEquals(shown, latest)) return id;
        var path = shown.PathFor(id);
        if (isFolder) return latest.FindFolder(path) is var folder and >= 0 ? folder : null;
        if (System.IO.Path.GetDirectoryName(path) is not { } parent || latest.FindFolder(parent) is not (var inside and >= 0)) return null;
        var children = new List<int>();
        latest.ChildIds(inside, children);
        var name = System.IO.Path.GetFileName(path);
        foreach (var child in children)
            if (latest.NameOf(child).Equals(name, StringComparison.OrdinalIgnoreCase)) return child;
        return null;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        PlaceLabels();
    }

    // ------------------------------------------------------------------ colour helpers

    private static uint Pack(Color color) => (uint)color.R << 16 | (uint)color.G << 8 | color.B;
    private static Color Unpack(uint color) => Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color);

    private static uint KindColor(int kind)
        => kind >= DiskUsagePalette.Other ? Pack(Color.FromRgb(0x6E, 0x69, 0x62)) : Palette[kind % Palette.Length];

    private static uint Shade(uint color, double factor)
    {
        var r = (uint)Math.Clamp((color >> 16 & 255) * factor, 0, 255);
        var g = (uint)Math.Clamp((color >> 8 & 255) * factor, 0, 255);
        var b = (uint)Math.Clamp((color & 255) * factor, 0, 255);
        // Quantised, so the palette texture stays a few hundred entries however many boxes there are.
        return (r & 0xFC) << 16 | (g & 0xFC) << 8 | (b & 0xFC);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
