namespace PlayMap.Engine.Tests;

/// <summary>A fact that runs only on Windows (the Windows CI job runs it), for a subject that exists there only — a named event and
/// its ACL. Elsewhere it is skipped with the words, never passed with nothing tested.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows only: the Windows CI job runs it";
    }
}
