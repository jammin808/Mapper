using PlayMap.Core.Media;

namespace PlayMap.Media.Tests.Hw;

/// <summary>A renderer's device as a test chooses it: its API, render node, what it imports, and the import failures reported to it.</summary>
internal sealed class FakeShare(GpuApi api, string? node = "/dev/dri/renderD128") : IGpuShare
{
    private readonly Dictionary<string, string> _failures = new(StringComparer.Ordinal);

    public GpuApi Api => api;
    public string Describe => "fake " + api;
    public nint D3D11Device => 0;
    public long AdapterLuid => 0;
    public string? DrmRenderNode => node;
    public long DeviceEpoch => 1;
    public bool DirectProven => true;
    public int Entered { get; private set; }
    public Func<uint, ulong, bool> Imports { get; set; } = (_, _) => true;

    public bool CanImport(GpuSurfaceFormat format, uint fourcc, ulong modifier) => Imports(fourcc, modifier);

    public void Enter() => Entered++;

    public void Exit() => Entered--;

    public void ReportImportFailure(string sourceKey, string words)
    {
        lock (_failures)
            _failures[sourceKey] = words;
    }

    public bool ImportFailed(string sourceKey, out string words)
    {
        lock (_failures)
            return _failures.TryGetValue(sourceKey, out words!);
    }
}
