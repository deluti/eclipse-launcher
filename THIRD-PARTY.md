# Third-party components

Eclipse embeds the Monocraft font under the SIL Open Font License. Its license
is included in the repository and installer. Self-contained Windows packages
include .NET runtime licenses and notices in the Notices directory.

The Linux desktop uses Avalonia 12.1.6 (MIT), SkiaSharp (MIT) and HarfBuzzSharp
(MIT) from their official NuGet packages. Release builds disable Avalonia and
.NET build telemetry. The published launcher performs no runtime telemetry.
Self-contained Linux archives include the corresponding .NET runtime notices.

Minecraft and Java are downloaded from their official distribution servers.
No skin agent, local texture server or optimization helper is bundled, downloaded
or injected into the game in version 0.5.0. An agent downloaded by an older
version is left on disk with the user's data and is never loaded by this version.
