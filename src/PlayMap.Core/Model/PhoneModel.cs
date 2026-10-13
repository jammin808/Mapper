namespace PlayMap.Core.Model;

/// <summary>A phone as it described itself when it joined; intrinsics solved with this profile are reused only while all of it matches.</summary>
/// <param name="Platform">"android-chrome" | "ios-safari" | "other".</param><param name="Exposure">"lock" | "steady".</param>
public sealed record PhoneProfile(string Platform, string Model, string CameraLabel, int Width, int Height, string Exposure);

public enum FolderAdvance { NewFile, Manual }

/// <summary>A camera that is a folder: each new image (NewFile) or SCAN NEXT (Manual) is the picture of the current pattern.</summary>
public sealed record FolderCapture(string Path, FolderAdvance Advance, int SettleMs);
