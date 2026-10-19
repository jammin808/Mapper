@rem The WiX toolset built from its MS-RL source (docs/round3/PLAN.md §3.5 item 2, M17), run from the checkout's src folder in a
@rem Visual Studio developer prompt: upstream's own build steps in upstream's order (src\build_all.cmd and each part's .cmd) for
@rem what PlayMap's installer would consume - the SDK and the Util and Firewall extensions with what they are built from - with
@rem upstream's own test runs left out (a third-party tool's internal suite; the job's summary says so). Nothing is downloaded from
@rem WiX's releases or packages: every WixToolset.* package restored here is one this build made.
@setlocal
@if "%~1"=="" (echo usage: build-toolset.cmd ^<wix checkout^>\src & exit /b 64)
@cd /d "%~1" || exit /b
@set _C=Release
@set _L=%~1\..\build\logs

call build_init.cmd || exit /b

pushd dtf
msbuild -Restore SfxCA\sfxca_t.proj -p:Configuration=%_C% -tl:off -nologo -m || exit /b
msbuild -Restore -t:Pack dtf.slnx -p:Configuration=%_C% -tl:off -nologo -m || exit /b
popd

pushd internal
nuget restore || exit /b
msbuild internal_t.proj -p:Configuration=%_C% -tl:off -nologo || exit /b
popd

pushd libs
msbuild -Restore libs_t.proj -p:Configuration=%_C% -tl:off -nologo -m || exit /b
popd

pushd api
msbuild api_t.proj -p:Configuration=%_C% -tl:off -nologo || exit /b
popd

pushd burn
nuget restore || exit /b
msbuild burn_t.proj -p:Configuration=%_C% -tl:off -nologo -m || exit /b
popd

pushd wix
msbuild -t:Restore wix.slnx -p:Configuration=%_C% -tl:off -nologo -m || exit /b
msbuild wixnative\wixnative_t.proj -p:Configuration=%_C% -tl:off -nologo -m || exit /b
msbuild wix.slnx -p:Configuration=%_C% -tl:off -nologo -m || exit /b
msbuild publish_t.proj -p:Configuration=%_C% -tl:off -nologo || exit /b
msbuild -t:Publish -p:Configuration=%_C% -tl:off -nologo WixToolset.Sdk\WixToolset.Sdk.csproj || exit /b
msbuild pack_t.proj -p:Configuration=%_C% -tl:off -nologo -m || exit /b
popd

pushd ext
msbuild -restore Util\util_t.proj -p:Configuration=%_C% -tl:off -nologo || exit /b
msbuild -restore Firewall\firewall_t.proj -p:Configuration=%_C% -tl:off -nologo || exit /b
popd

@endlocal
