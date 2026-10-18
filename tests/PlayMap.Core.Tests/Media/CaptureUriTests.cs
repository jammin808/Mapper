using PlayMap.Core.Media;
using PlayMap.Core.Wire;

namespace PlayMap.Core.Tests.Media;

/// <summary>The one capture grammar, <c>capture:&lt;provider&gt;/&lt;device&gt;[/&lt;input&gt;]</c>: each provider's form reads to its
/// fields and writes back canonical; an unknown provider, user information, a query and every other malformed form are refused with
/// words that repeat nothing of the URI; a provider whose code the node lacks answers not on this node yet; and a line carrying a
/// refused URI is written down without it.</summary>
public sealed class CaptureUriTests
{
    public static TheoryData<string, CaptureProvider, string, string?, string> Forms => new()
    {
        { "capture:dshow/Magewell USB Capture HDMI 4K+", CaptureProvider.Dshow, "Magewell USB Capture HDMI 4K+", null, "capture:dshow/Magewell USB Capture HDMI 4K+" },
        { "capture:v4l2/video0", CaptureProvider.V4l2, "video0", null, "capture:v4l2/video0" },
        { "capture:mf/Logitech BRIO", CaptureProvider.Mf, "Logitech BRIO", null, "capture:mf/Logitech BRIO" },
        { "capture:decklink/2", CaptureProvider.DeckLink, "2", null, "capture:decklink/2" },
        { "capture:decklink/DeckLink Duo (1)/sdi", CaptureProvider.DeckLink, "DeckLink Duo (1)", "sdi", "capture:decklink/DeckLink Duo (1)/sdi" },
        { "capture:aja/0/2", CaptureProvider.Aja, "0", "2", "capture:aja/0/2" },
        { "capture:magewell/Pro Capture Dual HDMI 4K/1", CaptureProvider.Magewell, "Pro Capture Dual HDMI 4K", "1", "capture:magewell/Pro Capture Dual HDMI 4K/1" },
        // The scheme and the provider ignore case and are written in lower case; a name keeps its case and its spaces.
        { "CAPTURE:DeckLink/Quad HDMI", CaptureProvider.DeckLink, "Quad HDMI", null, "capture:decklink/Quad HDMI" },
        { "  capture:V4L2/usb Cam  ", CaptureProvider.V4l2, "usb Cam", null, "capture:v4l2/usb Cam" },
        // The five escapes, either case of hex, read into the name and written back upper case.
        { "capture:dshow/Live Gamer 4K%2fHDR", CaptureProvider.Dshow, "Live Gamer 4K/HDR", null, "capture:dshow/Live Gamer 4K%2FHDR" },
        { "capture:mf/a%25b%3Fc%23d%40e/in%2F1", CaptureProvider.Mf, "a%b?c#d@e", "in/1", "capture:mf/a%25b%3Fc%23d%40e/in%2F1" },
    };

    [Theory]
    [MemberData(nameof(Forms))]
    public void EachProvidersFormReadsToItsFieldsAndWritesBackCanonical(string text, CaptureProvider provider, string device, string? input,
        string canonical)
    {
        Assert.True(CaptureUri.Claims(text));
        Assert.True(CaptureUri.TryParse(text, out var uri, out var words), words);
        Assert.Equal("", words);
        Assert.NotNull(uri);
        Assert.Equal(provider, uri.Provider);
        Assert.Equal(device, uri.Device);
        Assert.Equal(input, uri.Input);
        Assert.Equal(canonical, uri.ToString());
        Assert.Equal(CaptureUri.Word(provider), uri.ProviderWord);
        // The canonical form reads back as the same URI.
        Assert.True(CaptureUri.TryParse(canonical, out var again, out _));
        Assert.Equal(uri, again);
        Assert.Same(text, CaptureUri.Shown(text));
    }

    [Fact]
    public void EveryProviderHasAWordANameAndAFormAndNoOtherWordIsAProvider()
    {
        Assert.Equal(["dshow", "v4l2", "mf", "decklink", "aja", "magewell"], CaptureUri.ProviderWords);
        Assert.Equal(CaptureUri.ProviderWords, Enum.GetValues<CaptureProvider>().Select(CaptureUri.Word));
        Assert.Equal(["DirectShow", "Video4Linux2", "Media Foundation", "DeckLink", "AJA", "Magewell"],
            Enum.GetValues<CaptureProvider>().Select(CaptureUri.Name));
        foreach (var p in Enum.GetValues<CaptureProvider>())
        {
            Assert.Equal(p, CaptureUri.ProviderOf(CaptureUri.Word(p).ToUpperInvariant()));
            var made = CaptureUri.Of(p, "Cam 1", null, out var words);
            Assert.NotNull(made);
            Assert.Equal("", words);
            Assert.True(CaptureUri.TryParse(made.ToString(), out var read, out _));
            Assert.Equal(made, read);
        }
        foreach (var word in new[] { "", "ndi", "omt", "spout", "avfoundation", "dshow2", "video", "decklink " })
            Assert.Null(CaptureUri.ProviderOf(word));
    }

    /// <summary>Each refused form, with a token in it that the words must not repeat.</summary>
    public static TheoryData<string, string, string> Refused => new()
    {
        { "an unknown provider", "capture:zz9plural/cam", "one of the providers dshow, v4l2, mf, decklink, aja, magewell" },
        { "an unknown provider with an input", "capture:ndi/zz9host/zz9cam", "one of the providers" },
        { "user information in an authority", "capture://zz9user:zz9secret@dshow/cam", "carries no user information" },
        { "user information before the provider", "capture:zz9user:zz9secret@dshow/cam", "carries no user information" },
        { "user information in the device", "capture:dshow/zz9user:zz9secret@cam", "carries no user information" },
        { "an authority with no user information", "capture://zz9host/cam", "has no host part" },
        { "a query", "capture:dshow/cam?token=zz9secret", "carries no query" },
        { "a query on the input", "capture:decklink/2/sdi?key=zz9secret", "carries no query" },
        { "a fragment", "capture:dshow/cam#zz9part", "carries no fragment" },
        { "no device", "capture:dshow", "capture:<provider>/<device>[/<input>]" },
        { "an empty device", "capture:dshow/", "names its device" },
        { "a blank device", "capture:dshow/   ", "names its device" },
        { "an empty input", "capture:decklink/zz9card/", "names its input" },
        { "a third segment", "capture:decklink/zz9card/sdi/zz9extra", "capture:<provider>/<device>[/<input>]" },
        { "an escape the grammar does not read", "capture:dshow/zz9cam%20name", "escapes only %, /, ?, # and @" },
        { "a cut escape", "capture:dshow/zz9cam%2", "escapes only" },
        { "an escape to a control character", "capture:dshow/zz9cam%0A", "escapes only" },
        { "a control character", "capture:dshow/zz9cam\u0001", "holds no control character" },
        { "the scheme alone", "capture:", "capture:<provider>/<device>[/<input>]" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void AMalformedUriIsRefusedWithWordsThatRepeatNothingOfIt(string why, string text, string expected)
    {
        Assert.True(CaptureUri.Claims(text), why);
        Assert.False(CaptureUri.TryParse(text, out var uri, out var words), why);
        Assert.Null(uri);
        Assert.Contains(expected, words, StringComparison.Ordinal);
        Assert.DoesNotContain("zz9", words, StringComparison.Ordinal);
        Assert.Equal(CaptureUriVerdict.Refused, CaptureUri.Read(text, Enum.GetValues<CaptureProvider>()).Verdict);
        Assert.Equal(CaptureUri.RefusedMarker, CaptureUri.Shown(text));
    }

    [Fact]
    public void AUriLongerThanTheGrammarTakesAndTextThatIsNotACaptureUriAreRefused()
    {
        var longest = "capture:dshow/" + new string('c', CaptureUri.MaxLength - "capture:dshow/".Length);
        Assert.True(CaptureUri.TryParse(longest, out _, out _));
        Assert.False(CaptureUri.TryParse(longest + "c", out _, out var words));
        Assert.Equal("a capture URI is at most 512 characters", words);
        foreach (var other in new[] { null, "", "   ", "dshow/cam", "video=Cam", "ndi:HOST (Cam)", "/dev/video0", "capturex:dshow/cam" })
        {
            Assert.False(CaptureUri.Claims(other), other);
            Assert.False(CaptureUri.TryParse(other, out _, out words), other);
            Assert.Equal("a capture source is capture:<provider>/<device>[/<input>]", words);
        }
        Assert.Same("ndi:HOST (Cam)", CaptureUri.Shown("ndi:HOST (Cam)"));
        Assert.Null(CaptureUri.Of(CaptureProvider.Dshow, " ", null, out words));
        Assert.Equal("a capture URI names its device: capture:<provider>/<device>[/<input>]", words);
        Assert.Null(CaptureUri.Of((CaptureProvider)99, "cam", null, out words));
        Assert.StartsWith("a capture URI names one of the providers", words, StringComparison.Ordinal);
    }

    [Fact]
    public void AProviderWhoseCodeTheNodeLacksIsNotOnThisNodeYetAndOneItHasIsTaken()
    {
        CaptureProvider[] avdevice = [CaptureProvider.Dshow, CaptureProvider.V4l2];

        var taken = CaptureUri.Read("capture:v4l2/video0", avdevice);
        Assert.Equal(CaptureUriVerdict.Taken, taken.Verdict);
        Assert.Equal(CaptureProvider.V4l2, taken.Uri!.Provider);
        Assert.Equal("", taken.Words);

        var notYet = CaptureUri.Read("capture:decklink/2/sdi", avdevice);
        Assert.Equal(CaptureUriVerdict.NotOnThisNode, notYet.Verdict);
        Assert.Equal("2", notYet.Uri!.Device);
        Assert.Equal("DeckLink capture is not on this node yet", notYet.Words);
        Assert.Equal("Media Foundation capture is not on this node yet", CaptureUri.Read("capture:mf/Cam", avdevice).Words);
        Assert.Equal(CaptureUriVerdict.NotOnThisNode, CaptureUri.Read("capture:dshow/Cam", []).Verdict);

        var refused = CaptureUri.Read("capture:dshow/cam?zz9", avdevice);
        Assert.Equal(CaptureUriVerdict.Refused, refused.Verdict);
        Assert.Null(refused.Uri);
    }

    /// <summary>Wherever a refused capture URI rides in a line — a verb's argument, a quoted phrase, a JSON string, an unknown verb's
    /// word, the words before a secret — the line as it is written down holds the marker and nothing of the URI; a well-formed URI is
    /// written down as it came.</summary>
    [Fact]
    public void ALineCarryingARefusedUriIsWrittenDownWithoutIt()
    {
        string[] lines =
        [
            "INPUT ADD capture:dshow/cam?token=zz9secret Front",
            "INPUT ADD \"capture://zz9user:zz9secret@dshow/Cam Front\" Front",
            "OBSERVE op_1 FROM capture:zz9provider/zz9device",
            "BIND ADD {\"input\":\"OSC /x\",\"target\":\"INPUT ADD capture:dshow/cam?zz9secret\"}",
            "ADD surface {\"name\":\"capture:dshow/cam?zz9secret\"}",
            "NOSUCHVERB capture:dshow/cam?zz9secret",
        ];
        foreach (var text in lines)
        {
            Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
            var shown = VerbCatalog.Redacted(line);
            Assert.DoesNotContain("zz9", shown, StringComparison.Ordinal);
            Assert.Contains("‹refused›", shown, StringComparison.Ordinal);
        }
        foreach (var text in new[] { "INPUT ADD capture:dshow/Cam Front", "OBSERVE op_1 FROM \"capture:decklink/DeckLink Duo (1)/sdi\"" })
        {
            Assert.True(WireGrammar.TryParse(text, out var line, out _));
            Assert.Equal(line.Raw, VerbCatalog.Redacted(line));
        }
    }
}
