# Performance — 0.5.0

Version **0.5.1** disables ReadyToRun for the Eclipse assembly after a reproduced
x86 startup crash in the published 0.5.0 build. The ordinary JIT assembly survived
the same run; optimizations and game policy below are retained. Stock .NET runtime
assemblies are unchanged. Release CI now runs the actual published EXE normally
for 30 seconds per fixture, including cached skin/cape loading, instead of relying
only on an early-exit smoke test and a separately built test host.

All policy calculations and measurements are local. Eclipse adds no optimization
mods, changes no Minecraft options/resource packs, installs no services, and
uses no administrator privileges. Existing profiles and user data remain intact.

## Bottlenecks and changes

Previously every preparation deserialized the complete plan and receipt index,
revalidated URLs, queried each file twice and rewrote the whole index even if no
file changed. The launcher now retains one stamped plan and a preparation cache,
uses one file-stat result per file, and writes receipts only when they change.
Every launch still checks file existence, length and timestamp; changed files
are hashed and repaired from verified official downloads. Plan changes invalidate
the cache. Preparation caches are released after Java starts to reduce game-time
memory; the next launch rebuilds them locally.

The visible launcher previously continued rendering at 120 Hz during Minecraft.
Now game startup stops the animation timer **and its worker thread**, the sound
worker, cosmetic requests and pending update checks. Render buffers and hidden
gallery/skin pages are released. Draft file edits and the character pose survive.
One deferred, nonblocking optimized .NET collection can reclaim discarded caches;
there is no periodic collection or working-set trimming. Animation returns after
game exit. Screenshot and skin pages reuse unchanged content when switching tabs;
the default home skin is not decoded/rebuilt repeatedly during initialization.
The noise bitmap is generated using packed pixels and shared grain rows; golden
raw-pixel hashes verify that the old appearance is unchanged at both window sizes.

Game exit uses `WaitForExitAsync`, not a polling loop. Logs are streamed into a
bounded writer buffer rather than flushed for every line. Playtime keeps its
necessary 30-second persistence checkpoint; the one-second UI polling timer is
removed. A single resource sample runs three seconds into a game and is cancelled
if the game exits earlier. There is no permanent performance monitor.

## Java, RAM and Windows

The Minecraft version's official `javaVersion` selects the official runtime,
including loader/profile inheritance. New plans persist the required major
version. Existing runtime families remain supported. Local `release` metadata and
the executable PE header verify the Java major version and Windows x64 machine
type, without invoking a separate `java -version` process on each launch. Neither
PATH scanning nor replacing Java with a speculative newer runtime is used.

HotSpot keeps its normal GC ergonomics, which normally choose G1 on server-class
machines. Eclipse supplies only a modest `-Xms256M` and the memory budget through
`-Xmx`; it adds no GC/JIT/priority/FPS flag bundle. This follows
[Oracle's recommendation to begin with G1 defaults and heap sizing](https://docs.oracle.com/en/java/javase/25/gctuning/garbage-first-garbage-collector-tuning.html).
External JVM environment overrides are removed from the child environment, and
agent/native-injection flags and nested JVM argument files are rejected.

Fresh installations enable **Auto** memory. Older explicit memory values remain
manual. Switching modes saves both the mode and the manual value. Auto reads
physical RAM, available RAM and system-wide commit headroom with
[GetPerformanceInfo](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getperformanceinfo),
so the x86 launcher's address-space limit does not constrain the x64 game budget.
Its starting targets are 2 GiB for older Java/vanilla, 3 GiB for Java 21+ vanilla,
and 4/6/8 GiB for increasing mod counts. A read-only, bounded directory count is
the only mod inspection. Targets are constrained by available physical/commit
headroom, a native/Windows reserve and two-thirds of physical RAM, in 512 MiB
steps with a 1 GiB minimum. These are conservative heuristics, not promises that
every modpack fits. Manual settings are honored, including amounts above the
automatic recommendation. No mod or game configuration is rewritten.

**Settings → GPU** lists native DXGI hardware adapters and dedicated memory,
reads the Windows per-application GPU preference for the exact selected Java
executable, and shows where the user can change it in Windows. It does not force
a GPU or alter registry, driver, power, security, scheduling or priority settings.
Adapter availability does not prove which GPU Minecraft will actually use.

## Local measurements and reproduction

`data/diagnostics/startup.json` records first paint, ready time and launcher
resources. `launch.json` records the last launch's preparation time, file work,
process-start time, first output, an optional GUI-texture readiness marker, Java,
memory policy, one resource/GPU sample and exit code. Files are overwritten, not
accumulated indefinitely. They contain no authentication tokens, Minecraft account names or
command lines and are never uploaded. Missing GUI markers are not treated as a
failed launch: log formats differ between Minecraft versions.

Build the test project and run:

```powershell
dotnet tests/Eclipse.Tests/bin/Release/net10.0-windows/Eclipse.Tests.dll --benchmark <existing-data-directory> <result.json>
```

The harness performs six local preparations (the first includes cold application
code), then four preparations with caches released between attempts, and samples
an isolated launcher window for 2.5 seconds per state. Update responses are
simulated; no network or game/config/mod changes are needed. Game-start events
follow the same UI path as actual launches. Compare the same build type and
machine without concurrent compilation/compression. OS file caches and CPU load
still affect results.

## Measured comparison

Raw local results: [before](benchmarks/v0.5.0/before.json), [after](benchmarks/v0.5.0/after.json).

Same computer, x64 Release test host, installed 26.3 files, old 0.4.0 assembly and
new 0.5.0 assembly. Both builds were measured with the same harness. Warm results
exclude each process's first preparation. Medians use 5 warm or 4 cache-release
samples, respectively; UI samples are short observations, not broad benchmarks.

| Measurement | 0.4.0 | 0.5.0 |
| --- | ---: | ---: |
| Warm preparation median | 78.9 ms | 16.3 ms |
| Preparation after game-time cache release, median | 84.2 ms | 57.1 ms |
| Receipt rewrites for 6 unchanged preparations | 6 | 0 |
| Header frames/s while game is running, launcher visible | 120.1 | 0 |
| Launcher CPU while game runs, visible | 323 ms/s | 0–12 ms/s in repeated runs |
| Launcher private memory in visible game-time state | 93.6 MiB | 49.4 MiB |
| Launcher working set in that state | 137.9 MiB | 97.9 MiB |
| Managed heap in that state | 54.5 MiB | 8.9 MiB |

The final paired UI observation recorded first paint at 297 ms and 301 ms,
respectively, and Ready at 364 ms and 358 ms. Other runs varied; a faster first
window is **not demonstrated** by this small sample. Keeping startup local and
nonblocking remains the design. The clear gains are preparation I/O/allocation
and eliminating animation work during the game. CPU measurements near zero have
Windows process-time quantization limits.

Real Vanilla 26.3, Fabric 0.20.1 and Quilt 0.30.2 reached GUI textures using the
existing Java and isolated game directories, with no agents or test mods. Vanilla
GUI readiness was observed at 18.4 s before and 12.7 s after in one pair. This is
not a controlled game-load benchmark; OS caches, Java and the game account for
most of that time. No Minecraft FPS increase has been measured or claimed.

## Compatibility note: local skins

The old local-skin agent and loopback server were removed to satisfy the explicit
no-injection requirement. Saved PNGs and local preview selections remain intact;
Microsoft skin uploads are unchanged. A local nickname skin now affects the
launcher preview only. Previously downloaded helper files are left untouched and
are not loaded. Existing Fabric/Quilt profiles are launched as installed; Eclipse
does not add, remove or modify their mods.
