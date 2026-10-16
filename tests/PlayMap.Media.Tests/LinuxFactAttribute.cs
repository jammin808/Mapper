namespace PlayMap.Media.Tests;

/// <summary>A fact that runs only on Linux (its category is "Linux": the Linux CI job runs it), for a subject that exists there only —
/// VA-API through libva, a dmabuf share. Elsewhere it is skipped with the words, never passed with nothing tested.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Linux only: the Linux CI job runs it";
    }
}
