using System.Globalization;

namespace PlayMap.Net.Commands;

/// <summary>The words every transport says the same way (Patterns' wording with PlayMap's names, so a sender
/// reads the same correction from either app).</summary>
public static class WireWords
{
    public const string NotPaired =
        "not paired — this node asks for its pairing token first: AUTH-PROOF on the wire, X-PlayMap-Token on the web";

    public const string WrongToken = "wrong token — the pairing token is in PlayMap's Network drawer";

    public const string UseProof =
        "plain AUTH is for this machine only — answer the NONCE line: AUTH-PROOF <hex of HMAC-SHA256(token, nonce + your HELLO name)>";

    public const string Open = "open — this node asks for no token";

    public const string Paired = "paired";

    public const string WireOnly =
        "HELLO, NONCE, AUTH, AUTH-PROOF and SUBSCRIBE belong to the line wire — on the web the token rides in X-PlayMap-Token";

    public const string UseHttps =
        "This node requires HTTPS on the network — open https:// (the /tls page says how to trust its certificate).";

    public const string TooManyWrongTokens = "too many wrong tokens from this address — wait a minute";

    public const string TokenNeeded = "this node asks for its pairing token: send it in X-PlayMap-Token or Authorization: Bearer";

    /// <summary>A fault behind the wire is that line's: the type and the count, never the line (it may carry a
    /// token) and never the stack.</summary>
    public static string Fault(string type, long n) =>
        string.Create(CultureInfo.InvariantCulture, $"the node faulted on this line ({type}) — fault #{n}, logged");

    public const string Subscribe = "a subscription is SUBSCRIBE STATE|CONTEXT|PROJECT|OFF";
}
