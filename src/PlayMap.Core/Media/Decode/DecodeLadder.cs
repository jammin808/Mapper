using System.Globalization;

namespace PlayMap.Core.Media.Decode;

/// <summary>The best rung decoders opened from now on may take (<c>NodeSettings.Decode.Policy</c>, <c>DECODE POLICY</c>).</summary>
public enum DecodePolicy { Auto, Direct, GpuCopy, CopyBack, Software }

/// <summary>Why a source steps down the ladder at run time (zerocopy.md §3.4).</summary>
public enum DemotionTrigger
{
    /// <summary>The renderer could not import a frame (EGL_BAD_MATCH, EGL_BAD_ATTRIBUTE, EGL_BAD_ALLOC, an ANGLE refusal).</summary>
    ImportFailed,
    /// <summary>The first frame's layout (fourcc, modifier) is not one the renderer imports.</summary>
    NotImportable,
    /// <summary>The decoder waited more than three frame periods for a free surface, three times in ten seconds.</summary>
    Starved,
    /// <summary>The renderer's device changed under the source (its context was rebuilt): its surfaces no longer import there.</summary>
    DeviceLost,
    /// <summary>The direct-sampling proof failed: sources opened afterwards take one GPU copy.</summary>
    ProofFailed,
    /// <summary>The device opened but the decoder hands this stream back to software.</summary>
    HandedBack,
}

/// <summary>What the renderer's device offers decoders: its API, whether zero-copy imports are ready, whether direct sampling of
/// decoder surfaces is proven, and the words when zero-copy is not ready.</summary>
public readonly record struct ZeroCopyPlatform(GpuApi Api, bool Ready, bool DirectProven, string Words)
{
    public static readonly ZeroCopyPlatform None = new(GpuApi.None, false, false, "no renderer shares its device yet");
}

/// <summary>The rung a source opens on: the best path the policy, the platform and the proof allow, the hardware device it asks
/// for ("vaapi", "d3d11va", "" for the platform's own order), and why it is not a better one ("" when it is the best there is).</summary>
public readonly record struct LadderChoice(DecodePath Ceiling, string Device, string Why);

/// <summary>The decode-path ladder as pure rules: direct → one GPU copy → copy-back → software (zerocopy.md §3.4). A source opens on
/// the highest rung allowed by the policy, the renderer's device and the proof; at run time it only ever steps down, one rung or
/// straight to copy-back, keeping its queue (the renderer holds the last picture), and never steps back up until it is opened
/// again. HapGpu and GpuShare are set by their own producers and are not rungs of this ladder.</summary>
public static class DecodeLadder
{
    /// <summary>A decoder waited for a free surface longer than this many frame periods: one starved wait.</summary>
    public const int StarvedFramePeriods = 3;

    /// <summary>This many starved waits inside <see cref="StarvedWindowNs"/> demote the source.</summary>
    public const int StarvedWaits = 3;

    public const long StarvedWindowNs = 10_000_000_000;

    /// <summary>The policy's word (auto, direct, gpu-copy, copy-back, software).</summary>
    public static string Word(DecodePolicy policy) => policy switch
    {
        DecodePolicy.Direct => "direct",
        DecodePolicy.GpuCopy => "gpu-copy",
        DecodePolicy.CopyBack => "copy-back",
        DecodePolicy.Software => "software",
        _ => "auto",
    };

    /// <summary>Reads a policy word, either case; false for anything else (the wire refuses it).</summary>
    public static bool TryParse(string? word, out DecodePolicy policy)
    {
        switch ((word ?? "").Trim().ToLowerInvariant())
        {
            case "auto":
                policy = DecodePolicy.Auto;
                return true;
            case "direct":
                policy = DecodePolicy.Direct;
                return true;
            case "gpu-copy":
                policy = DecodePolicy.GpuCopy;
                return true;
            case "copy-back":
                policy = DecodePolicy.CopyBack;
                return true;
            case "software":
                policy = DecodePolicy.Software;
                return true;
            default:
                policy = DecodePolicy.Auto;
                return false;
        }
    }

    /// <summary>A path's word in STATE (hw-direct, hw-gpu-copy, hap-gpu, gpu-share, hw-copy-back, software).</summary>
    public static string Word(DecodePath path) => path switch
    {
        DecodePath.HwDirect => "hw-direct",
        DecodePath.HwGpuCopy => "hw-gpu-copy",
        DecodePath.HapGpu => "hap-gpu",
        DecodePath.GpuShare => "gpu-share",
        DecodePath.HwCopyBack => "hw-copy-back",
        _ => "software",
    };

    /// <summary>The ladder's order: 1 is the best rung (direct), 4 software; the paths of other producers rank 0.</summary>
    public static int Rank(DecodePath path) => path switch
    {
        DecodePath.HwDirect => 1,
        DecodePath.HwGpuCopy => 2,
        DecodePath.HwCopyBack => 3,
        DecodePath.Software => 4,
        _ => 0,
    };

    /// <summary>The best rung a policy allows.</summary>
    public static DecodePath Ceiling(DecodePolicy policy) => policy switch
    {
        DecodePolicy.GpuCopy => DecodePath.HwGpuCopy,
        DecodePolicy.CopyBack => DecodePath.HwCopyBack,
        DecodePolicy.Software => DecodePath.Software,
        _ => DecodePath.HwDirect,
    };

    /// <summary>What a share offers decoders (None for no share).</summary>
    public static ZeroCopyPlatform Platform(IGpuShare? share, bool ready, string words) =>
        share is null ? ZeroCopyPlatform.None : new ZeroCopyPlatform(share.Api, ready, share.DirectProven, words);

    /// <summary>The rung a source opens on. Linux (a dmabuf share) imports VAAPI surfaces in place, always direct; Windows (the
    /// D3D11 share) takes one GPU copy until direct sampling is proven on the driver, or when the policy says direct and the
    /// operator forced it (<paramref name="forced"/>); every other renderer gets frames back in system memory.</summary>
    public static LadderChoice Choose(DecodePolicy policy, ZeroCopyPlatform platform, bool forced = false)
    {
        if (policy == DecodePolicy.Software)
            return new(DecodePath.Software, "", "decode.policy is software");
        if (policy == DecodePolicy.CopyBack)
            return new(DecodePath.HwCopyBack, "", "decode.policy is copy-back: frames come back to system memory");
        if (!platform.Ready)
            return new(DecodePath.HwCopyBack, "", platform.Words.Length > 0 ? platform.Words : "zero-copy is not ready on this renderer");
        switch (platform.Api)
        {
            case GpuApi.DmaBuf:
                // VAAPI images are sampled in place or not at all: there is no one-copy rung between direct and copy-back.
                return policy == DecodePolicy.GpuCopy
                    ? new(DecodePath.HwCopyBack, "vaapi", "decode.policy is gpu-copy: VAAPI has no one-copy path, so frames come back to system memory")
                    : new(DecodePath.HwDirect, "vaapi", "");
            case GpuApi.D3D11:
                if (policy == DecodePolicy.GpuCopy)
                    return new(DecodePath.HwGpuCopy, "d3d11va", "decode.policy is gpu-copy");
                if (platform.DirectProven || (policy == DecodePolicy.Direct && forced))
                    return new(DecodePath.HwDirect, "d3d11va", "");
                return new(DecodePath.HwGpuCopy, "d3d11va", "direct sampling is not proven on this driver: one GPU copy a frame");
            default:
                return new(DecodePath.HwCopyBack, "", "zero-copy needs a dmabuf (EGL) or D3D11 renderer: frames come back to system memory");
        }
    }

    /// <summary>The rung a source steps down to on a trigger, and the words why; a trigger that does not apply to the rung answers
    /// the same path and no words.</summary>
    public static (DecodePath Next, string Why) Demote(DecodePath current, DemotionTrigger trigger, string detail)
    {
        var c = CultureInfo.InvariantCulture;
        var tail = string.IsNullOrWhiteSpace(detail) ? "" : ": " + detail.Trim();
        return trigger switch
        {
            DemotionTrigger.ImportFailed when Rank(current) is 1 or 2 =>
                (DecodePath.HwCopyBack, $"the renderer could not import a frame{tail}; frames come back to system memory until the clip is opened again"),
            DemotionTrigger.NotImportable when Rank(current) is 1 or 2 =>
                (DecodePath.HwCopyBack, $"the first frame's layout does not import here{tail}; frames come back to system memory"),
            DemotionTrigger.Starved when Rank(current) is 1 or 2 =>
                (DecodePath.HwCopyBack, string.Create(c, $"the decoder waited over {StarvedFramePeriods} frames for a free surface {StarvedWaits} times in {StarvedWindowNs / 1_000_000_000} s{tail}; frames come back to system memory")),
            DemotionTrigger.ProofFailed when current == DecodePath.HwDirect =>
                (DecodePath.HwGpuCopy, $"the direct-sampling proof failed{tail}; one GPU copy a frame"),
            DemotionTrigger.HandedBack when current != DecodePath.Software =>
                (DecodePath.Software, $"the device handed this stream back to software{tail}"),
            DemotionTrigger.DeviceLost when Rank(current) is 1 or 2 =>
                (DecodePath.HwCopyBack, $"the renderer's device changed{tail}; frames come back to system memory until the clip is opened again"),
            _ => (current, ""),
        };
    }
}

/// <summary>The starvation watch of one hardware source: a wait for a free surface longer than three frame periods is one starved
/// wait; the third inside ten seconds trips it. Times are the caller's monotonic nanoseconds. One writer (the decode thread);
/// allocation-free.</summary>
public sealed class StarvationWatch
{
    private readonly long[] _at = new long[DecodeLadder.StarvedWaits];
    private int _count;

    /// <summary>Starved waits counted inside the window at the last report.</summary>
    public int Recent { get; private set; }

    /// <summary>Records a finished wait for a surface; true when it trips the demotion.</summary>
    public bool Waited(long waitNs, long framePeriodNs, long nowNs)
    {
        if (framePeriodNs <= 0 || waitNs <= DecodeLadder.StarvedFramePeriods * framePeriodNs)
            return false;
        _at[_count % _at.Length] = nowNs;
        _count++;
        var recent = 0;
        for (var i = 0; i < Math.Min(_count, _at.Length); i++)
            if (nowNs - _at[i] <= DecodeLadder.StarvedWindowNs)
                recent++;
        Recent = recent;
        return Recent >= DecodeLadder.StarvedWaits;
    }

    /// <summary>Forgets every wait (the source reopened).</summary>
    public void Reset()
    {
        Array.Clear(_at);
        _count = 0;
        Recent = 0;
    }
}
