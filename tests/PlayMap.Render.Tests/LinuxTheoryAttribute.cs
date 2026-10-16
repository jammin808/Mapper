namespace PlayMap.Render.Tests;

/// <summary>A theory that runs only on Linux, as <see cref="LinuxFactAttribute"/>: every case is skipped elsewhere with the words,
/// never passed with nothing tested.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class LinuxTheoryAttribute : TheoryAttribute
{
    public LinuxTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Linux only: the Linux CI job runs it";
    }
}
