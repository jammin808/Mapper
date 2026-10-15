#!/usr/bin/env bash
# Runs a command as on a bare machine with Microsoft's .NET (the CI runner, a fresh user machine), in a private mount
# namespace so nothing else on this machine changes. Run as root.
#  - Each library named in HIDE_LIBS (default: VA-API and OpenAL, which a bare Ubuntu lacks and a machine with a distro
#    FFmpeg has) is covered with /dev/null in the system library directory, so dlopen fails for it.
#  - A distro-built .NET (Ubuntu's dotnet packages) carries a runtime graph in Microsoft.NETCore.App.deps.json that maps
#    the distro's RID (ubuntu.24.04-x64) to linux-x64; Microsoft's portable runtime has none, and loaders that read the
#    graph (Silk.NET's) then miss runtimes/linux-x64. With PORTABLE_GRAPH=1 (default) that graph is covered with an
#    empty one, as on Microsoft's runtime.
#   tools/without-libs.sh dotnet test tests/PlayMap.Media.Tests -c Debug --no-build
#   HIDE_LIBS="libopenal" PORTABLE_GRAPH=0 tools/without-libs.sh <command...>
set -euo pipefail
LIBDIR=${LIBDIR:-/usr/lib/x86_64-linux-gnu}
HIDE_LIBS=${HIDE_LIBS-"libva libva-drm libva-x11 libopenal"}
PORTABLE_GRAPH=${PORTABLE_GRAPH:-1}
[ "$#" -gt 0 ] || { echo "usage: $0 <command...>" >&2; exit 2; }
script='set -e'
for name in $HIDE_LIBS; do
  for f in "$LIBDIR/$name".so*; do
    [ -e "$f" ] || continue
    script="$script; mount --bind /dev/null '$(readlink -f "$f")'"
  done
done
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
if [ "$PORTABLE_GRAPH" = 1 ]; then
  root=$(dirname "$(readlink -f "$(command -v dotnet)")")
  i=0
  for deps in "$root"/shared/Microsoft.NETCore.App/*/Microsoft.NETCore.App.deps.json; do
    [ -e "$deps" ] || continue
    grep -q '"runtimes"' "$deps" || continue
    i=$((i + 1))
    python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); d.pop("runtimes",None); json.dump(d,open(sys.argv[2],"w"),indent=2)' "$deps" "$work/deps$i.json"
    script="$script; mount --bind '$work/deps$i.json' '$deps'"
  done
fi
unshare -m --propagation private bash -c "$script; exec \"\$@\"" bash "$@"
