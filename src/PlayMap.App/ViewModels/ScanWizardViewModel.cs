using System.Collections.ObjectModel;
using System.Globalization;
using PlayMap.App.Infrastructure;
using PlayMap.App.Link;
using PlayMap.App.State;
using PlayMap.Core.Model;

namespace PlayMap.App.ViewModels;

/// <summary>One step of the scan wizard: its number, its name, and the one sentence that says what it is
/// about to do.</summary>
public sealed class ScanStep(int number, string title) : ObservableObject
{
    private string _sentence = "";
    private bool _isCurrent;
    private bool _isDone;

    public int Number { get; } = number;

    public string Title { get; } = title;

    public string Sentence
    {
        get => _sentence;
        set => Set(ref _sentence, value);
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }

    public bool IsDone
    {
        get => _isDone;
        set => Set(ref _isDone, value);
    }
}

public enum ScanMode
{
    Fast,
    Robust,
    Difficult,
}

public sealed class ProjectorPick(string id, string name, string color) : ObservableObject
{
    private bool _isPicked = true;
    private double _progress;
    private string _state = "";

    public string Id { get; } = id;

    public string Name { get; } = name;

    public string Color { get; } = color;

    public bool IsPicked
    {
        get => _isPicked;
        set => Set(ref _isPicked, value);
    }

    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }

    public string State
    {
        get => _state;
        set => Set(ref _state, value);
    }
}

/// <summary>SCAN: a five-step wizard that hides the structured-light machinery — 1 camera, 2 projectors,
/// 3 scan, 4 review (smart-scan picture, confidence, the regions found; click one to make a surface),
/// 5 apply. Each step says in one sentence what it is about to do; the scan's progress is STATE.scan.</summary>
public sealed class ScanWizardViewModel : ObservableObject
{
    private readonly DeskSession _session;
    private int _current = 1;
    private ItemChoice? _camera;
    private IReadOnlyList<ItemChoice> _cameras = [];
    private CameraSourceKind _newCameraKind = CameraSourceKind.Device;
    private string _newCameraSource = "video0";
    private bool _lockExposure = true;
    private ScanMode _mode = ScanMode.Robust;
    private string _area = "Whole picture";
    private ScanState? _scan;
    private string _progressText = "";
    private double _progress;
    private string _qualityText = "";
    private double _quality;
    private bool _showConfidence;
    private ScanRegion? _selectedRegion;
    private string _applied = "";

    public ScanWizardViewModel(DeskSession session)
    {
        _session = session;
        Steps =
        [
            new ScanStep(1, "Camera"),
            new ScanStep(2, "Projectors"),
            new ScanStep(3, "Scan"),
            new ScanStep(4, "Review"),
            new ScanStep(5, "Apply"),
        ];
        Next = new RelayCommand(() => Current++, () => CanNext);
        Back = new RelayCommand(() => Current--, () => _current > 1);
        GoTo = new RelayCommand(p =>
        {
            if (p is ScanStep step && step.Number <= MaxReachable)
                Current = step.Number;
        });
        AddCamera = DeskAction.Send(session, "addCamera", "Add this camera", () => "ADD camera " + VerbText.Json(new CameraRig("", NewCameraName, _newCameraKind,
            _newCameraSource, Intrinsics.FromFieldOfView(1920, 1080, 70), LensDistortion.None, Pose.Identity, false))).Look("plus");
        Start = DeskAction.Send(session, "start", "Scan", StartVerb, isEnabled: () => _camera is not null && Projectors.Any(p => p.IsPicked) && _scan is not { IsRunning: true })
            .Look("scan", danger: true);
        Cancel = DeskAction.Send(session, "cancel", "Cancel scan", () => "SCAN CANCEL", isEnabled: () => _scan is { IsRunning: true }).Look("stop");
        MakeSurface = DeskAction.Send(session, "makeSurface", "Make surface", () => _scan is { Id.Length: > 0 } s && _selectedRegion is { } r
            ? string.Create(CultureInfo.InvariantCulture, $"SCAN SURFACE {s.Id} {VerbText.Num(r.X + r.W / 2)} {VerbText.Num(r.Y + r.H / 2)} {VerbText.Word(Capital(r.Label))}")
            : null, isEnabled: () => _selectedRegion is not null).Look("surface");
        // A scan block without an id (a view being taken before the scan is named) has nothing to apply.
        Apply = DeskAction.Run(session, "apply", "Apply scan", ApplyAsync, () => _scan is { Id.Length: > 0 } s ? "SCAN APPLY " + s.Id : null,
            isEnabled: () => _scan is { IsDone: true }).Look("scan");
        UpdateSentences();
        Steps[0].IsCurrent = true;
    }

    public IReadOnlyList<ScanStep> Steps { get; }

    /// <summary>The node the desk is on (its preview rings are named after it).</summary>
    public string NodeId => _session.NodeId;

    public RelayCommand Next { get; }

    public RelayCommand Back { get; }

    public RelayCommand GoTo { get; }

    public DeskAction AddCamera { get; }

    public DeskAction Start { get; }

    public DeskAction Cancel { get; }

    public DeskAction MakeSurface { get; }

    public DeskAction Apply { get; }

    public ObservableCollection<ProjectorPick> Projectors { get; } = [];

    public ObservableCollection<ScanRegion> Regions { get; } = [];

    public IReadOnlyList<CameraSourceKind> CameraKinds { get; } = [CameraSourceKind.Device, CameraSourceKind.Phone, CameraSourceKind.Folder, CameraSourceKind.Ndi];

    public IReadOnlyList<ScanMode> Modes { get; } = Enum.GetValues<ScanMode>();

    public IReadOnlyList<string> Areas { get; } = ["Whole picture", "Inner 90 %", "Inner 75 %"];

    public int Current
    {
        get => _current;
        set
        {
            var v = Math.Clamp(value, 1, 5);
            if (!Set(ref _current, v))
                return;
            for (var i = 0; i < Steps.Count; i++)
            {
                Steps[i].IsCurrent = i + 1 == v;
                Steps[i].IsDone = i + 1 < v;
            }
            Raise(nameof(CurrentStep), nameof(Sentence), nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(IsStep4), nameof(IsStep5), nameof(CanNext));
            Next.RaiseCanExecuteChanged();
            Back.RaiseCanExecuteChanged();
        }
    }

    public ScanStep CurrentStep => Steps[_current - 1];

    /// <summary>What the current step is about to do.</summary>
    public string Sentence => CurrentStep.Sentence;

    public bool IsStep1 => _current == 1;

    public bool IsStep2 => _current == 2;

    public bool IsStep3 => _current == 3;

    public bool IsStep4 => _current == 4;

    public bool IsStep5 => _current == 5;

    /// <summary>A step is passable when what it chooses is chosen (a camera; a projector; a finished scan).</summary>
    public bool CanNext => _current switch
    {
        1 => _camera is not null,
        2 => Projectors.Any(p => p.IsPicked),
        3 => _scan is { IsDone: true },
        4 => _scan is { IsDone: true },
        _ => false,
    };

    private int MaxReachable => _camera is null ? 1 : !Projectors.Any(p => p.IsPicked) ? 2 : _scan is { IsDone: true } ? 5 : 3;

    public IReadOnlyList<ItemChoice> Cameras
    {
        get => _cameras;
        private set => Set(ref _cameras, value);
    }

    public ItemChoice? Camera
    {
        get => _camera;
        set
        {
            if (Set(ref _camera, value))
                Changed();
        }
    }

    public CameraSourceKind NewCameraKind
    {
        get => _newCameraKind;
        set
        {
            if (Set(ref _newCameraKind, value))
            {
                NewCameraSource = value switch
                {
                    CameraSourceKind.Phone => "phone",
                    CameraSourceKind.Folder => "scans/photos",
                    CameraSourceKind.Ndi => "STUDIO (Camera 1)",
                    _ => "video0",
                };
                Raise(nameof(NewCameraHint));
            }
        }
    }

    public string NewCameraSource
    {
        get => _newCameraSource;
        set
        {
            if (Set(ref _newCameraSource, value))
                AddCamera.Refresh();
        }
    }

    public string NewCameraHint => _newCameraKind switch
    {
        CameraSourceKind.Phone => "Open the node's /scan page on the phone and stand it near the projector lens.",
        CameraSourceKind.Folder => "Photograph each pattern by hand from a tripod; drop the photos in this folder.",
        CameraSourceKind.Ndi => "An NDI source name on the show network.",
        _ => "A webcam or capture device on this machine — put it near the projector lens.",
    };

    private string NewCameraName => _newCameraKind switch
    {
        CameraSourceKind.Phone => "Phone",
        CameraSourceKind.Folder => "Photo folder",
        CameraSourceKind.Ndi => "NDI camera",
        _ => "Webcam",
    };

    /// <summary>Lock exposure, white balance and focus before the patterns run (the scan fails on a camera
    /// that hunts).</summary>
    public bool LockExposure
    {
        get => _lockExposure;
        set
        {
            if (Set(ref _lockExposure, value))
                Changed();
        }
    }

    public ScanMode Mode
    {
        get => _mode;
        set
        {
            if (Set(ref _mode, value))
                Changed();
        }
    }

    public string Area
    {
        get => _area;
        set
        {
            if (Set(ref _area, value))
                Changed();
        }
    }

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public string QualityText
    {
        get => _qualityText;
        private set => Set(ref _qualityText, value);
    }

    public double Quality
    {
        get => _quality;
        private set => Set(ref _quality, value);
    }

    public bool IsRunning => _scan is { IsRunning: true };

    public bool IsDone => _scan is { IsDone: true };

    public string? ScanId => _scan?.Id;

    /// <summary>Review shows the confidence heatmap instead of the smart-scan picture.</summary>
    public bool ShowConfidence
    {
        get => _showConfidence;
        set
        {
            if (Set(ref _showConfidence, value))
                Raise(nameof(ReviewViewId));
        }
    }

    /// <summary>The engine's preview view for the review picture.</summary>
    public string ReviewViewId => _scan is null ? "scan-none" : $"scan-{_scan.Id}-{(_showConfidence ? "confidence" : "smart")}";

    public ScanRegion? SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (Set(ref _selectedRegion, value))
                MakeSurface.Refresh();
        }
    }

    public string Applied
    {
        get => _applied;
        private set => Set(ref _applied, value);
    }

    /// <summary>A click on the review picture (0..1 across and down): the smallest region under it.</summary>
    public void PickAt(double x, double y) =>
        SelectedRegion = Regions.Where(r => x >= r.X && y >= r.Y && x < r.X + r.W && y < r.Y + r.H).OrderBy(r => r.W * r.H).FirstOrDefault();

    private string? StartVerb()
    {
        if (_camera is null)
            return null;
        var picked = Projectors.Where(p => p.IsPicked).Select(p => p.Id).ToList();
        if (picked.Count == 0)
            return null;
        return $"SCAN START {_camera.Id} {string.Join(' ', picked)} MODE {_mode.ToString().ToUpperInvariant()}";
    }

    private async Task ApplyAsync(CancellationToken ct)
    {
        if (_scan is not { Id.Length: > 0 } s)
            return;
        var reply = await _session.SendAsync("SCAN APPLY " + s.Id, ct);
        if (reply.Ok)
        {
            Applied = reply.Text.Length > 0 ? DeskSession.Plain(reply.Text) : "Applied.";
            _session.Notify(ToastKind.Done, "Scan applied — " + Applied);
        }
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public void OnProject(Project p)
    {
        Cameras = p.Stage.Cameras.Select(c => new ItemChoice(c.Id, $"{c.Name} · {c.SourceKind}")).ToList();
        var keep = _camera?.Id;
        _camera = Cameras.FirstOrDefault(c => c.Id == keep) ?? (Cameras.Count > 0 ? Cameras[^1] : null);
        Raise(nameof(Camera));
        Projectors.SyncById(p.Stage.Projectors, x => x.Id, x => x.Id, x =>
        {
            var pick = new ProjectorPick(x.Id, x.Name, x.Color);
            pick.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ProjectorPick.IsPicked))
                    Changed();
            };
            return pick;
        }, (_, _) => { });
        Raise(nameof(NodeId));
        Changed();
    }

    public void OnState(StateSnapshot s)
    {
        _scan = s.Scan;
        if (_scan is { } scan)
        {
            Progress = scan.Progress;
            ProgressText = scan.State switch
            {
                "running" or "starting" => string.Create(CultureInfo.InvariantCulture, $"{Capital(scan.Stage)} · pattern {scan.Pattern} of {scan.Patterns} · {scan.Progress * 100:0} %"),
                "done" => "Done.",
                "cancelled" => scan.Message.Length > 0 ? scan.Message : "Cancelled.",
                "failed" => scan.Message.Length > 0 ? "Failed — " + scan.Message : "Failed.",
                _ => scan.State,
            };
            Quality = scan.Quality;
            QualityText = scan.IsDone ? scan.QualityWords : "";
            foreach (var pp in scan.Projectors)
                if (Projectors.FirstOrDefault(p => p.Id == pp.Id) is { } pick)
                {
                    pick.Progress = pp.Progress;
                    pick.State = pp.State;
                }
            Regions.SyncById(scan.Regions, r => r.Id, r => r.Id, r => r, (_, _) => { });
            if (_selectedRegion is not null && !Regions.Contains(_selectedRegion))
                SelectedRegion = Regions.FirstOrDefault(r => r.Id == _selectedRegion.Id);
        }
        Raise(nameof(IsRunning), nameof(IsDone), nameof(ScanId), nameof(ReviewViewId));
        Changed();
    }

    private void Changed()
    {
        UpdateSentences();
        Raise(nameof(CanNext), nameof(Sentence));
        Next.RaiseCanExecuteChanged();
        new[] { AddCamera, Start, Cancel, MakeSurface, Apply }.RefreshAll();
    }

    private void UpdateSentences()
    {
        var picked = Projectors.Where(p => p.IsPicked).Select(p => p.Name).ToList();
        Steps[0].Sentence = _camera is null
            ? "Pick the camera that will watch the patterns — or add one — and stand it near the projector lens."
            : $"The scan will watch through {_camera.Name}{(_lockExposure ? ", with its exposure, white balance and focus locked first" : "")}.";
        Steps[1].Sentence = picked.Count == 0
            ? "Tick the projectors to scan."
            : $"{string.Join(", ", picked)} will show {_mode.ToString().ToLowerInvariant()} patterns over {_area.ToLowerInvariant()} — {ModeWords}.";
        Steps[2].Sentence = _scan switch
        {
            { IsRunning: true } => "Scanning — the outputs show patterns until it is done; Cancel gives them back at once.",
            { IsDone: true } => "The scan is done — review what it found.",
            _ => $"Pressing Scan takes the picked outputs off the show for {ScanSeconds} while the patterns run.",
        };
        Steps[3].Sentence = _scan is { IsDone: true }
            ? "This is what the camera saw, from the projector's point of view. Click an object to make it a surface."
            : "The review appears when a scan is done.";
        Steps[4].Sentence = _scan is { IsDone: true }
            ? "Apply turns the scan into the mesh, the projectors' poses and the surfaces you made."
            : "Apply is ready after a scan.";
    }

    private string ModeWords => _mode switch
    {
        ScanMode.Fast => "about 22 patterns each, for matte surfaces in a dark room",
        ScanMode.Difficult => "about 72 patterns each, for shiny, dark or inter-reflecting materials",
        _ => "about 44 patterns each, the right choice most of the time",
    };

    private string ScanSeconds
    {
        get
        {
            var n = Math.Max(1, Projectors.Count(p => p.IsPicked));
            var each = _mode switch { ScanMode.Fast => 6, ScanMode.Difficult => 20, _ => 12 };
            return string.Create(CultureInfo.InvariantCulture, $"about {each * n} seconds");
        }
    }
}
