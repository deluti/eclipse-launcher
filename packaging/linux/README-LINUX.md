# Eclipse Launcher for Arch Linux

The portable build is self-contained and does not require a system .NET runtime.
Extract it and run `./Eclipse`. The native Arch package can be installed with:

```bash
sudo pacman -U eclipse-launcher-bin-*.pkg.tar.zst
```

Eclipse stores its files in `${XDG_DATA_HOME:-$HOME/.local/share}/eclipse-launcher`.
Set `ECLIPSE_DATA_DIR` to use another location. Microsoft session data is stored
with Unix mode `0600`; the launcher does not start a keyring helper or service.

The build supports x86_64 Arch Linux and Arch-derived distributions with glibc,
including Omarchy. X11 and native Wayland sessions are supported by Avalonia.
Minecraft uses Mojang's official Linux x64 Java runtime selected by the chosen
Minecraft version. Fabric and Quilt can be installed from the main window.
