namespace PlayMap.Core.Machine;

/// <summary>The names a supervisor and the engine it started share, beside the files in its folder (<see cref="EnginePidRecord"/>,
/// <see cref="SupervisorStatus"/>).</summary>
public static class SupervisorNames
{
    /// <summary>The engine's stop event, made by the supervisor, the only one that sets or clears it: its ACL gives the show user
    /// Synchronize alone, so the engine started with <c>--supervised</c> opens it with that right and waits on it, stops as any asked
    /// stop does once it is set, and is terminated when it still runs at the end of the supervisor's grace.</summary>
    public const string StopEvent = @"Global\PlayMap-Engine-Stop";
}
