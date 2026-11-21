#!/usr/bin/env bash
# The priority probe's fence on every push (the Linux test job, and any bench): the probe built from a copy outside the checkout, the
# way the prioritycost job builds it (tools/ci-tests.ps1 -PriorityCost), so a change to what it references under src/ fails here and not
# at the next dispatch; then its reader run over the hand-made legs in tools/priority-probe/fixture (a stall that straddles the window's
# start, a frozen tick, a pool item still pending at the window's end, one pending at the window's start, one that never ran, a burning
# lead, a second run of one kind), each figure the fixture's expected.csv names held to its value and each line its expected-summary.tsv
# names looked for in summary.md: a reader that regresses fails the step with the words of what it no longer counts.
#   tools/priority-probe/selfcheck.sh <folder outside the checkout> [dotnet build arguments...]
# The copy is built in Release in a fresh folder the script makes inside the one given (priority-probe.XXXXXX) and removes at its end:
# nothing the script did not make is ever deleted. The build arguments go to dotnet build as given (the Linux job passes the run's
# -p:PlayMapRun and -p:PlayMapCommit, the ones its solution build stamped, so the projects the copy references build as they were).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
checkout="$(cd "$here/../.." && pwd)"
given="${1:?say a folder outside the checkout to build the copy in}"
shift
mkdir -p "$given"
given="$(cd "$given" && pwd)"
case "$given/" in
  "$checkout"/*) echo "selfcheck.sh: $given is inside the checkout; the copy is built outside it, where the tree's Directory.Build.props does not reach" >&2; exit 2 ;;
esac
out="$(mktemp -d "$given/priority-probe.XXXXXX")"
trap 'rm -rf -- "$out"' EXIT
cp "$here"/*.cs "$out"/
cp "$here/PlayMap.PriorityProbe.csproj.in" "$out/PlayMap.PriorityProbe.csproj"
dotnet build "$out/PlayMap.PriorityProbe.csproj" -c Release -v minimal "-p:PlayMapCheckout=$checkout/" "$@"
dotnet "$out/bin/Release/net10.0/PlayMap.PriorityProbe.dll" selfcheck --fixture "$here/fixture"
