using PlayMap.Core.Model;

namespace PlayMap.Engine.Scanning;

/// <summary>The camera a rig is, for anything that captures through one (a scan, an align run, a camera view).</summary>
public static class ScanCameras
{
    /// <summary>A phone through the node's phone hub, a folder of photos, else a capture device or a stream; null when this node cannot open the rig's kind
    /// (a phone rig on a node without the hub among them).</summary>
    public static IScanCamera? Open(EngineCore? engine, CameraRig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        if (rig.SourceKind == CameraSourceKind.Phone)
            return engine?.Services.Phones is { } hub ? new PhoneCamera(hub, rig) : null;
        if (rig.SourceKind == CameraSourceKind.Folder)
            return new FolderCamera(rig);
        return CaptureCamera.OptionsFor(rig) is { } options ? new CaptureCamera(options) : null;
    }

    /// <summary>The camera rig a line names: by its id, or "phone:&lt;phone id&gt;" for the rig a phone joined as; null when none.</summary>
    public static CameraRig? Find(Project project, string word)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(word);
        const string Phone = "phone:";
        if (!word.StartsWith(Phone, StringComparison.OrdinalIgnoreCase))
            return project.Stage.Cameras.Find(word);
        var phone = word[Phone.Length..];
        return project.Stage.Cameras.FirstOrDefault(c => c.SourceKind == CameraSourceKind.Phone && string.Equals(c.Source, phone, StringComparison.Ordinal));
    }
}
