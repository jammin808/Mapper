# The WiX toolset's local feed

The `wix-toolset` CI job fills this folder with the packages it builds from WiX's MS-RL source at the pinned tag
(`installer/wix/toolset.json`): `WixToolset.Sdk` and the Util and Firewall extensions. The root `nuget.config` maps `WixToolset.*`
to this folder and nowhere else. No package is committed here.
