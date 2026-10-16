namespace PlayMap.Render.Tests;

/// <summary>A fact that runs only on Linux (its subject — an EGL or GLX context, a dmabuf import or export — exists there only: the
/// Linux CI job runs it). Elsewhere it is skipped with the words, never passed with nothing tested.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Linux only: the Linux CI job runs it";
    }
}
