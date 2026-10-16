# libvmx (PlayMap)

Upstream: https://github.com/openmediatransport/libvmx at `a1828cb` (23 Sep 2026), vendored unchanged under `src/` and
`build/` with the upstream `LICENSE.txt` (MIT; `src/sse2neon.h` carries its own MIT notice). It is OMT's video codec, a native
library loaded at run time as `libvmx` (`libvmx.so`, `libvmx.dylib`, `libvmx.dll`): PlayMap's tests build it here with the
upstream command (`build/buildlinuxx64.sh`, clang++), and the node finds it through `PLAYMAP_VMX_DIR`, beside the exe, or in
`runtimes/<rid>/native`. No patches.
