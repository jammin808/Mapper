using PlayMap.Core.Model;

namespace PlayMap.Engine.Scanning;

/// <summary>The camera a rig is, for anything that captures through one (a scan, an align run, a camera view).</summary>
public static class ScanCameras
{
    /// <summary>A phone through the node's phone hub, else a capture device or a stream; null when this node cannot open the rig's kind
    /// (a phone rig on a node without the hub among them).</summary>
    public static IScanCamera? Open(EngineCore? engine, CameraRig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        if (rig.SourceKind == CameraSourceKind.Phone)
            return engine?.Services.Phones is { } hub ? new PhoneCamera(hub, rig) : null;
        return CaptureCamera.OptionsFor(rig) is { } options ? new CaptureCamera(options) : null;
    }
}
