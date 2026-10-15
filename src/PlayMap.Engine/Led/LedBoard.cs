using PlayMap.Core.Bus;
using PlayMap.Core.Led;
using PlayMap.Core.Model;
using PlayMap.Render.Led;

namespace PlayMap.Engine.Led;

/// <summary>The engine's side of LED output: the plan of the published rig (rebuilt when the rig, a canvas, an output or a surface
/// changes — reference equality — and handed to the sender as a program), the rig's live air (level, test, locate: machine-local,
/// journalled, never an undo step), the machine master, the render host's pass, and the sender. Any thread reads; the lane writes
/// the live air and the master.</summary>
internal sealed class LedBoard
{
    private readonly Lock _gate = new();
    private readonly EngineServices _services;
    private LedPlan _plan = LedPlan.Empty;
    private (LedRig? Rig, object? Canvases, object? Outputs, object? Surfaces, int MaxMbps) _key;
    private long _serial;
    private volatile LedLive _live = LedLive.Initial;
    private volatile LedPass? _pass;
    private volatile bool _master;
    private string _settingsProblem = "";

    public LedBoard(EngineServices services)
    {
        _services = services;
        _master = services.Settings.Led.Output;
        services.LedOutput?.SetMaster(_master, blackFirst: false);
    }

    /// <summary>The sender (null: this node sends no LED data).</summary>
    public ILedOutput? Output => _services.LedOutput;

    /// <summary>True when this node has a renderer that samples LEDs.</summary>
    public bool HasRenderer => _services.Outputs is Rendering.RenderHost;

    /// <summary>The machine master: this machine transmits LED data.</summary>
    public bool Master => _master;

    public LedLive Live => _live;

    public LedPass? Pass => _pass;

    /// <summary>Why the last change to the master could not be written to the settings file ("" when it was).</summary>
    public string SettingsProblem => _settingsProblem;

    public void Attach(LedPass? pass) => _pass = pass;

    /// <summary>The plan last built (the render thread reads it every frame).</summary>
    public LedPlan Current => _plan;

    /// <summary>Builds and publishes the plan of the project the store just published (the store's change handler on the lane and
    /// the engine's constructor only: one writer of the plan and of the sender's program). The same rig, canvases, outputs and
    /// surfaces (as instances) keep the plan already published.</summary>
    public LedPlan Publish(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        lock (_gate)
        {
            if (Matches(project))
                return _plan;
            var maxMbps = _services.Settings.Led.MaxMbps;
            var plan = LedPlanBuilder.Build(project, maxMbps, ++_serial);
            _plan = plan;
            _key = (project.Leds, Box(project.Canvases), Box(project.Outputs), Box(project.Stage.Surfaces), maxMbps);
            Output?.Apply(LedPrograms.Build(plan, LedPrograms.CidOf(_services.Settings.NodeId), "PlayMap " + _services.Settings.Name));
            return plan;
        }
    }

    /// <summary>The plan of a project as a reader sees it: the published one when the project is the one it was built from, else
    /// a plan of that project built for the reader alone (never published, never sent).</summary>
    public LedPlan PlanFor(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        lock (_gate)
        {
            if (Matches(project))
                return _plan;
        }
        return Trial(project);
    }

    private bool Matches(Project project) =>
        _serial > 0 && ReferenceEquals(_key.Rig, project.Leds) && ReferenceEquals(_key.Canvases, Box(project.Canvases))
        && ReferenceEquals(_key.Outputs, Box(project.Outputs)) && ReferenceEquals(_key.Surfaces, Box(project.Stage.Surfaces))
        && _key.MaxMbps == _services.Settings.Led.MaxMbps;

    /// <summary>An immutable array's backing array (its identity says whether the list changed).</summary>
    private static object Box<T>(System.Collections.Immutable.ImmutableArray<T> a) =>
        System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(a) ?? (object)Array.Empty<T>();

    /// <summary>A plan of a project the store has not published (to check an edit before it is made); the published plan stays.</summary>
    public LedPlan Trial(Project project) => LedPlanBuilder.Build(project, _services.Settings.Led.MaxMbps, -1);

    public void SetLive(Func<LedLive, LedLive> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        lock (_gate)
            _live = edit(_live);
    }

    /// <summary>Turns the machine master on or off (off sends one black frame first unless held) and writes it to the settings file.</summary>
    public void SetMaster(bool on, bool hold)
    {
        _master = on;
        Output?.SetMaster(on, blackFirst: !hold);
        var settings = _services.Settings.Led with { Output = on };
        _settingsProblem = _services.LedPersist?.Invoke(settings) ?? "";
    }
}
