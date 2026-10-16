using System.Collections.Concurrent;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Devices.Native;
using PlayMap.Engine.Devices;

namespace PlayMap.Engine;

/// <summary>
/// The double-correction guard (autoalign M4, the read half): a projector whose own geometry or edge blend is on while PlayMap draws a
/// measured correction on the output it shows warps or blends twice. For every device linked to an output with a measured correction
/// (by the link's output, or by the projector the output shows), the projector's own processing is read live in step with the device's
/// poll, off the lane; one found on lights its <c>device:&lt;id&gt;</c> context item amber with the words and the line that turns it off
/// (DEVICE RAW), and STATE.align.devices says what was read. DEVICE NATIVE reads one projector now. Nothing is ever written to it.
/// </summary>
public sealed partial class EngineCore
{
    /// <summary>A device's last read: the outputs it guards, what was read (null when the read failed) and when, and the words.</summary>
    private sealed record NativeView(string DeviceId, IReadOnlyList<string> Outputs, NativeReading? Reading, long AtUnixMs, string Words);

    private readonly ConcurrentDictionary<string, NativeView> _native = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _nativeDue = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _nativeBusy = new(StringComparer.Ordinal);

    /// <summary>The outputs with a measured correction a device's projector shows.</summary>
    private static List<Output> GuardedOutputs(Project p, DeviceLink link)
    {
        var list = new List<Output>();
        foreach (var o in p.Outputs)
            if (o.Measured is not null && (string.Equals(o.Id, link.OutputId, StringComparison.Ordinal)
                    || (link.ProjectorId.Length > 0 && string.Equals(o.ProjectorId, link.ProjectorId, StringComparison.Ordinal))))
                list.Add(o);
        return list;
    }

    private static async Task<NativeReading?> ReadNativeAsync(DeviceService devices, DeviceLink link, CancellationToken ct)
    {
        NativeReading? reading = null;
        await devices.RunAsync(link.Id, "read its own processing", async (d, c) =>
        {
            reading = await NativeProcessing.ReadAsync(d, c).ConfigureAwait(false);
            return reading.Result;
        }, ct).ConfigureAwait(false);
        return reading;
    }

    /// <summary>The guard's words for a device: which of its own processing fights which outputs, or null when nothing does.</summary>
    private static (string Words, string Fix)? Fight(DeviceLink link, NativeView view)
    {
        if (view.Reading is not { } r || view.Outputs.Count == 0 || !(r.GeometryOn || r.BlendOn))
            return null;
        var on = r.Settings.Where(s => s.On).ToList();
        var what = string.Join(" and ", on.Select(s => s.Name));
        var outputs = string.Join(", ", view.Outputs);
        var fight = r.GeometryOn ? "two warps fight" : "two blends fight";
        return ($"{link.Name}'s own {what} {(on.Count == 1 ? "is" : "are")} on while PlayMap corrects {outputs} — {fight}",
            $"DEVICE RAW {link.Id} {NativeProcessing.NeutralLine(on[0])}");
    }

    /// <summary>The lane's tick: a read is started for each guarded device when it is due (its poll interval, at least a second), one
    /// at a time per device, never once the node is stopping; a device that guards nothing any more is forgotten.</summary>
    private void TickNative()
    {
        if (Services.Devices is not { } devices || Stopping)
            return;
        var p = Store.Project;
        var now = Services.Mono.NowNs;
        foreach (var link in p.Stage.Devices)
        {
            var guarded = GuardedOutputs(p, link);
            if (guarded.Count == 0)
            {
                _nativeDue.TryRemove(link.Id, out _);
                if (_native.TryRemove(link.Id, out _))
                    FactsChanged();
                continue;
            }
            if (_nativeDue.TryGetValue(link.Id, out var due) && now < due)
                continue;
            if (!_nativeBusy.TryAdd(link.Id, true))
                continue;
            _nativeDue[link.Id] = now + Math.Max(1000, link.PollIntervalMs) * 1_000_000L;
            var outputs = guarded.Select(o => o.Id).ToList();
            _ = Task.Run(async () =>
            {
                try
                {
                    var reading = await ReadNativeAsync(devices, link, CancellationToken.None).ConfigureAwait(false);
                    _native[link.Id] = new NativeView(link.Id, outputs, reading, Services.Wall.UnixMs, reading?.Words ?? "not read");
                    FactsChanged();
                }
                catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or InvalidOperationException or IOException)
                {
                    // The node is stopping or the projector went away mid-read: the read is told as failed and the next poll tries again.
                    _native[link.Id] = new NativeView(link.Id, outputs, null, Services.Wall.UnixMs, $"not read: {ex.Message}");
                }
                finally
                {
                    _nativeBusy.TryRemove(link.Id, out _);
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>A guarded device's next read is due now (an accept just put a measured correction on its output).</summary>
    private void NativeDueNow()
    {
        foreach (var key in _nativeDue.Keys)
            _nativeDue[key] = long.MinValue;
    }

    /// <summary>DEVICE NATIVE &lt;device&gt;: the projector's own geometry and edge blend, read now, with the outputs it guards and, when one
    /// fights a measured correction, the words and the line that turns it off.</summary>
    private async Task<CommandReply> DeviceNativeAsync(VerbCall c)
    {
        if (c.Args.Count != 1)
            return CommandReply.Fail("DEVICE NATIVE <device>");
        if (ResolveDevice(c.Args[0]) is not { } link)
            return CommandReply.Fail($"no projector link {c.Args[0]} — DEVICE LIST names them");
        if (Services.Devices is not { } devices)
            return CommandReply.Fail("projector control is not on this node");
        var (_, problem) = DeviceService.ToProjectorLink(link);
        if (problem.Length > 0)
            return CommandReply.Fail($"{link.Name}: {problem}");
        var reading = await ReadNativeAsync(devices, link, c.Ct).ConfigureAwait(false);
        if (reading is null)
            return CommandReply.Fail($"{link.Name}: its own processing was not read");
        var outputs = GuardedOutputs(Store.Project, link).Select(o => o.Id).ToList();
        var view = new NativeView(link.Id, outputs, reading, Services.Wall.UnixMs, reading.Words);
        if (outputs.Count > 0)
        {
            _native[link.Id] = view;
            FactsChanged();
        }
        if (!reading.Read && reading.Settings.IsEmpty)
            return CommandReply.Fail($"{link.Name}: {reading.Words}");
        var fight = Fight(link, view);
        var words = $"{link.Name}: {reading.Words}" + (fight is { } f ? $" — {f.Words}; {f.Fix} turns it off" : "");
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("device", link.Id);
            w.WriteString("protocol", link.Protocol);
            w.WriteStartArray("settings");
            foreach (var s in reading.Settings)
            {
                w.WriteStartObject();
                w.WriteString("name", s.Name);
                w.WriteString("word", s.Word);
                w.WriteString("value", s.Value);
                w.WriteString("neutral", s.Neutral);
                w.WriteBoolean("on", s.On);
                w.WriteBoolean("verified", s.Verified);
                w.WriteString("caveat", s.Caveat);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("unsupported");
            foreach (var u in reading.Unsupported)
                w.WriteStringValue(u);
            w.WriteEndArray();
            w.WriteStartArray("guards");
            foreach (var o in outputs)
                w.WriteStringValue(o);
            w.WriteEndArray();
            w.WriteString("fix", fight?.Fix ?? "");
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    /// <summary>align.devices: every guarded device's last read — its outputs, whether its own geometry and blend are on, when, the words
    /// and the fix when they fight.</summary>
    private void WriteNative(Utf8JsonWriter w)
    {
        var p = _snapshot.Project.Project;
        w.WriteStartArray("devices");
        foreach (var link in p.Stage.Devices)
        {
            if (!_native.TryGetValue(link.Id, out var view))
                continue;
            var fight = Fight(link, view);
            w.WriteStartObject();
            w.WriteString("id", link.Id);
            w.WriteStartArray("outputs");
            foreach (var o in view.Outputs)
                w.WriteStringValue(o);
            w.WriteEndArray();
            w.WriteBoolean("read", view.Reading is { Read: true });
            w.WriteBoolean("geometry", view.Reading?.GeometryOn ?? false);
            w.WriteBoolean("blend", view.Reading?.BlendOn ?? false);
            w.WriteNumber("at", view.AtUnixMs);
            w.WriteString("light", fight is null ? "green" : "amber");
            w.WriteString("words", fight?.Words ?? view.Words);
            w.WriteString("fix", fight?.Fix ?? "");
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>Every device whose own processing fights a measured correction, as last read: its id, name, the words and the fix line.</summary>
    internal List<(string Id, string Name, string Words, string Fix)> NativeFights()
    {
        var list = new List<(string, string, string, string)>();
        foreach (var link in Store.Project.Stage.Devices)
            if (_native.TryGetValue(link.Id, out var view) && Fight(link, view) is { } fight)
                list.Add((link.Id, link.Name, fight.Words, fight.Fix));
        return list;
    }

    /// <summary>A device whose own processing fights a measured correction: its context item amber (never lowered from red) with the
    /// guard's words and fix.</summary>
    private void NativeContext(List<ContextItem> items)
    {
        var p = _snapshot.Project.Project;
        foreach (var link in p.Stage.Devices)
        {
            if (!_native.TryGetValue(link.Id, out var view) || Fight(link, view) is not { } fight)
                continue;
            var i = items.FindIndex(x => x.Id == "device:" + link.Id);
            var words = $"{fight.Words}; {fight.Fix}";
            if (i < 0)
                items.Add(new ContextItem("device:" + link.Id, "device", link.Name, Light.Amber, words));
            else
                items[i] = items[i] with { Light = items[i].Light == Light.Red ? Light.Red : Light.Amber, State = words };
        }
    }
}
