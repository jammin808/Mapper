"""The .editorconfig generator (inherited from Patterns round 70). Every rule the build fails on is named here with its reason;
LANDED says which families have had their instances fixed and so stand as errors now; the rest of a
family's rules sit at suggestion until it lands. Re-run to regenerate .editorconfig at the repo root."""
import sys, pathlib

LANDED = set(sys.argv[1:]) if len(sys.argv) > 1 else set()

# family -> [(rule, reason)]
FENCES = {
 "culture": [
  ("CA1305", "a number or date formatted without a provider follows the machine's locale: 0,5 on a German desk, a Unicode minus on a Swedish one; the wire, the files and the words say the same on every machine"),
  ("CA1310", "StartsWith/EndsWith/IndexOf/Compare without a StringComparison are culture-sensitive and slow; the wire's verbs compare ordinally"),
  ("S6580", "a Parse without a provider reads 0.5 as 5 on a machine whose decimal mark is a comma"),
 ],
 "disposal": [
  ("CA2213", "a disposable field the owner never disposes leaks for the length of the show: paints, listeners, tokens, timers"),
  ("CA1001", "a type that owns a disposable field is disposable itself"),
  ("S2930", "an IDisposable made and dropped is a leak"),
  ("S3881", "the dispose pattern done by halves double-frees or never frees"),
  ("S1215", "GC.Collect is a pause on the show; ShowGc governs the collector"),
 ],
 "threading": [
  ("CA2008", "a task started without a scheduler runs on whatever scheduler is current: the desk's UI thread, when a tick started it"),
  ("VSTHRD105", "a ContinueWith without a scheduler runs on whatever scheduler is current"),
  ("CA1849", "a blocking call inside an async method holds the thread the method was meant to free"),
  ("VSTHRD103", "a blocking call inside an async method holds the thread the method was meant to free"),
  ("VSTHRD002", "Result, Wait and GetResult on the UI thread are a hang when the task needs that thread to finish"),
  ("VSTHRD100", "an async void method drops its exceptions on the floor and cannot be awaited"),
  ("VSTHRD101", "an async void lambda drops its exceptions on the floor and cannot be awaited"),
  ("VSTHRD110", "an async result nobody observes is an attempt, not a fact"),
  ("CA2002", "a lock on a string, a type or a member is shared with strangers across the process"),
  ("S3998", "a lock on a string, a type or a member is shared with strangers across the process"),
  ("CA2016", "a cancellation token not forwarded leaves the cancelled work running"),
  ("S8949", "a cancellation token not forwarded leaves the cancelled work running"),
  ("CA2012", "a ValueTask consumed twice or stored is undefined behaviour"),
 ],
 "security": [
  ("CA5392", "a P/Invoke without a search path takes a DLL planted beside a portable exe: the system's libraries come from System32 by name"),
  ("CA5351", "MD5 and DES are broken; where a protocol demands one (PJLink's digest) the site says so"),
  ("CA5399", "an HttpClient that skips certificate revocation trusts a revoked certificate"),
  ("S4036", "a command run by name is resolved through PATH: the shell that opens a folder is named by its full path"),
  ("S5443", "a shared temp directory is writable by every process on the machine"),
  ("S2245", "randomness that stands in for a secret comes from the cryptographic generator; a shuffle says it is a shuffle"),
  ("S4790", "a weak hash used as a secret; where a protocol demands one the site says so"),
 ],
 "correctness": [
  ("CA1806", "a result ignored is an attempt taken for a fact: a keep-awake that failed, a registry write that failed"),
  ("CA2211", "a mutable static field is shared state with no owner"),
  ("CA1851", "an IEnumerable enumerated twice runs its query twice"),
  ("CA1068", "the cancellation token is the last parameter"),
  ("CA1513", "ObjectDisposedException.ThrowIf says what it checks"),
  ("CA2020", "a pointer narrowed to an int truncates silently past 2 GB"),
  ("CA1812", "an internal class nothing instantiates is dead code; one that reflection builds says so"),
  ("S1751", "a loop that runs once is a bug or a lie"),
  ("S2123", "an increment whose result is dropped does nothing"),
  ("S2178", "a non-short-circuit boolean hides which side ran"),
  ("S2234", "arguments in the parameters' names but another order are swapped until proven otherwise; name them"),
  ("S2486", "an exception caught and ignored is an attempt taken for a fact; log it or say why not"),
  ("S108", "an empty block says nothing; a comment says why it is empty"),
  ("S1854", "a value stored and never read is a bug or dead code"),
  ("S1144", "a private member nothing uses is dead code"),
  ("S4487", "a private field written and never read is dead code"),
  ("S1481", "a local nothing reads is dead code"),
  ("S3237", "a setter that ignores value is a bug"),
  ("S4275", "a getter or setter on the wrong field is a bug"),
  ("S6561", "elapsed time from a wall clock jumps with NTP and daylight saving; Stopwatch or the show clock measure it"),
  ("S6444", "a regular expression without a timeout on text from the wire or a page is a hang waiting for its input"),
  ("S6562", "a DateTime built without a Kind is neither local nor UTC until someone guesses"),
  ("S8969", "a null-forgiving operator the compiler does not need hides the day it does"),
  ("S2681", "a line indented under an if or a loop that is not in it misleads the reader"),
  ("S2699", "a test without an assertion proves nothing"),
  ("S2701", "asserting a constant proves nothing"),
 ],
 "performance": [
  ("CA1869", "a JsonSerializerOptions per call rebuilds its metadata cache each time"),
  ("CA1870", "a SearchValues instance searches many characters in one pass"),
  ("CA1875", "Regex.Count does not materialise the matches"),
  ("CA1865", "the char overload avoids a string search"),
  ("CA1866", "the char overload avoids a string search"),
  ("CA1826", "an indexer beats LINQ on a list"),
  ("CA1827", "Any beats Count for emptiness"),
  ("CA1829", "the Count property beats the Count method"),
  ("CA1860", "Count > 0 beats Any on a collection"),
  ("CA1815", "a struct compared without Equals boxes; the maths structs implement equality"),
  ("S1155", "Any beats Count() for emptiness"),
 ],
 "banned": [
  ("RS0030", "BannedSymbols.txt: Thread.Sleep, Environment.TickCount, GC.Collect, Console.Write, Task.Wait — each with its reason beside it"),
  ("RS0031", "a banned symbol listed twice"),
 ],
}

# rules explicitly off, with the reason (SDK and Sonar noise for an app like this one)
OFF = [
 ("CA1062", "nullable reference types are on; the compiler does the null checks"),
 ("CA1031", "the show path catches everything by design (nothing throws through a frame); S2486 keeps the catch honest"),
 ("CA2007", "the desk's async code continues on the UI context on purpose; the libraries are called from the tick"),
 ("CA1307", "Equals and Contains are ordinal already; CA1310 fences the culture-sensitive ones"),
 ("CA1308", "ToLowerInvariant is the house spelling of a case fold"),
 ("CA1515", "the desk is an application, not a library: public types are its shape"),
 ("CA1002", "List<T> in an API is the house shape"), ("CA1003", "event handler signatures are the house shape"),
 ("CA1030", "naming"), ("CA1034", "nested types are used for scoping"), ("CA1051", "public fields in structs and DTOs are the house shape"),
 ("CA1054", "URIs travel as strings on the wire"), ("CA1055", "URIs travel as strings on the wire"), ("CA1056", "URIs travel as strings on the wire"),
 ("CA1707", "test names carry underscores"), ("CA1711", "naming"), ("CA1716", "naming"), ("CA1720", "naming"), ("CA1724", "naming"), ("CA1725", "naming"),
 ("CA1805", "explicit initialisation to the default is house style where it says something"),
 ("CA1814", "jagged arrays are the house shape for grids"), ("CA1819", "array properties are the house shape"),
 ("CA2234", "URIs travel as strings on the wire"),
 ("CA5394", "the desk's randomness is show-visual and audience-play; the pairing token uses the cryptographic generator (S2245 fences that)"),
 ("CA1416", None),  # keep default (warning->error): platform compatibility; not off — listed to say so
 ("S3358", "nested conditionals are the house style for a word chosen from a state"),
 ("S3267", "a loop turned into LINQ allocates in paths that must not"),
 ("S1135", "TODO is not used; the papers carry what is next"), ("S125", "commented code is removed by review, not by a rule"),
 ("S1075", "the desk's own URIs are constants"), ("S1313", "the desk binds its own addresses by design"),
 ("S101", "naming"), ("S1118", "static classes are the house shape"), ("S1199", "nested blocks scope a lock or a span"),
 ("S1905", "explicit casts say what they mean"), ("S1125", "explicit boolean comparison is used for clarity"),
 ("S1066", "nested ifs are used for clarity"), ("S907", "goto is used in a parser by design"),
 ("S3218", "nested types are used for scoping"), ("S3903", "top-level types in tests"), ("S4136", "overload order follows the reading order"),
 ("S1871", "identical branches are sometimes the clearest table"), ("S3220", "overload resolution is what it is"),
 ("S2292", "trivial properties are written out where they say something"), ("S1104", "public fields in DTOs are the house shape"),
 ("S3604", "member initialisers and constructor assignments are both used"), ("S2325", "a method on an instance stays where it reads"),
 ("CA1822", "a method on an instance stays where it reads"), ("S6608", "First and Last read as the intent"),
 ("S6610", "StartsWith(string) reads as the intent"), ("S4200", "the P/Invoke classes are internal wrappers already"),
 ("S6640", "unsafe code is the frame path by design"), ("S5332", "the desk's own HTTP on the show LAN is by design; the token fences it"),
 ("S2925", "a test may sleep to let a worker run"), ("S1643", "string concatenation in a loop is used where the count is small"),
 ("S2376", "write-only properties are used as commands"), ("S3973", "indentation of a conditional is the reader's"),
 ("S3241", "a return value dropped is sometimes the intent (fluent calls)"), ("S2479", "control characters in literals are the wire's own"),
 ("VSTHRD200", "the Async suffix is not the house naming"), ("VSTHRD003", "tasks are awaited where they are made; the desk has no JoinableTaskFactory"),
 ("VSTHRD010", "no JoinableTaskFactory"), ("VSTHRD011", "no JoinableTaskFactory"), ("VSTHRD111", "the desk's async code continues on the UI context on purpose"),
]

# rules kept as suggestions: seen, judged worth a look, not a stop
SUGGEST = [
 ("CA2000", "dispose before losing scope: many false positives where the object is handed on; reviewed by hand"),
 ("CA1867", "a single-char string compared with a comparison other than Ordinal: the analyzer itself calls the char overload unsafe here (a case-insensitive suffix is not one char), so the five sites stay as they are and a new one is judged, not fenced"),
 ("CA1861", "a constant array in an argument: 27 sites in the source, all cold; reviewed by hand where a path is hot"),
 ("CA1859", "a concrete type over an interface in a private signature: a virtual call saved, a shape the readers know lost; by hand where a path is hot"),
 ("CA1508", "dead conditional code: the flow analysis is wrong about volatile fields and loops here"),
 ("CA2216", "finalizers for handle owners: the services dispose deterministically and the census counts them"),
 ("CA1063", "the dispose pattern in full is not the house shape for sealed classes"),
 ("CA2201", "reserved exception types, in tests"), ("CA2263", "generic overload"), ("CA2025", "disposable passed to a task"),
 ("S1244", "floating-point equality: a sentinel compared exactly is intended; reviewed by hand"),
 ("S1172", "unused parameters: many are interface-shaped or handlers"), ("S927", "parameter names against the interface"),
 ("S2365", "array-returning properties copy"), ("S2696", "instance methods writing static fields"),
 ("S2386", "mutable public statics"), ("S3887", "readonly arrays are still mutable"), ("S1450", "a field that could be a local"),
 ("S127", "a loop counter modified in the loop"), ("S6966", "an async overload exists"), ("S2933", "a field that could be readonly"),
 ("S1994", "a for loop's stop condition"),
]

def lines():
    out = []
    out.append("# Round 70 — the analyzers as fences. Generated by tools/gen_editorconfig.py; edit that file, not this one.")
    out.append("# A rule at error stops the build (TreatWarningsAsErrors is on). A rule at suggestion shows in the IDE and")
    out.append("# is reviewed by hand. A rule at none is off, with the reason. Families not yet landed sit at suggestion.")
    out.append("root = true")
    out.append("")
    out.append("[*.cs]")
    out.append("charset = utf-8")
    out.append("end_of_line = lf")
    out.append("insert_final_newline = true")
    out.append("indent_style = space")
    out.append("indent_size = 4")
    for fam, rules in FENCES.items():
        out.append("")
        out.append(f"# ---- {fam}: {'landed — errors' if fam in LANDED else 'not landed yet — suggestions until its instances are fixed'} ----")
        for rule, reason in rules:
            out.append(f"# {rule}: {reason}")
            out.append(f"dotnet_diagnostic.{rule}.severity = {'error' if fam in LANDED else 'suggestion'}")
    out.append("")
    out.append("# ---- kept as suggestions: seen, worth a look, not a stop ----")
    for rule, reason in SUGGEST:
        if reason: out.append(f"# {rule}: {reason}")
        out.append(f"dotnet_diagnostic.{rule}.severity = suggestion")
    out.append("")
    out.append("# ---- off, with the reason ----")
    for rule, reason in OFF:
        if reason is None: continue
        out.append(f"# {rule}: {reason}")
        out.append(f"dotnet_diagnostic.{rule}.severity = none")
    out.append("")
    out.append("[tests/**/*.cs]")
    out.append("# The tests: culture-sensitive formatting, blocking waits, a plain HttpClient against a fake server, a forced collection in a lifecycle test and a sleep for a worker are the test's own business; constant arrays in asserts are fine; a redundant ! or a date literal without a Kind in a test is noise, not a fence.")
    for rule in ("CA1305", "CA1310", "S6580", "CA1861", "VSTHRD002", "S1481", "S6444", "CA1812", "CA5399", "CA2000", "CA2213", "S2925", "S1215", "S3881", "CA5351", "S4790", "CA1849", "VSTHRD103", "VSTHRD105", "CA2008", "S8969", "S6562"):
        out.append(f"dotnet_diagnostic.{rule}.severity = suggestion")
    out.append("")
    return "\n".join(out) + "\n"

pathlib.Path(".editorconfig").write_text(lines())
print("editorconfig written; landed:", sorted(LANDED) or "none")
