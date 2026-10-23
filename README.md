[English](README.md) | [简体中文](README.zh-CN.md)

# Godot

Godot 4.5.1 Mono/.NET host for Windows, Linux and macOS. Place it at `Godot/` beside Shared and Tools. Runtime content comes from workspace `Content/`; native libraries are prepared for `win-x64`, `linux-x64` or `osx-x64`. Apple silicon uses the x64 Godot .NET editor under Rosetta because the original FMOD 1.10 runtime is x64.

Follow the adjacent Tools README with `-Targets Godot`. Preparation requires PowerShell 7, .NET SDK 10, CMake 3.24+ and the platform's C++ toolchain. Supply the corresponding original game runtime libraries and audio banks.

Import `project.godot`, open `Scenes/Bootstrap.tscn` and run with F5. Choose Original or InstalledEverest under Launch / Source Profile. Auto selects D3D11 on Windows and Vulkan on Linux/macOS; Windows also supports explicit Vulkan and D3D12. Linux supports X11 and Wayland. macOS Vulkan uses MoltenVK. Command-line user arguments can include `-- --everest --graphics-backend=vulkan`.

Install matching .NET export templates and run `Tools/Export.ps1 -GodotExecutable <path-to-Godot-Mono>`. Optional `-RuntimeIdentifier win-x64`, `linux-x64` or `osx-x64` selects the prepared target runtime. Finalize Linux exports on Linux and macOS exports on macOS to preserve native library aliases; macOS also requires signing the completed app bundle. Windows cross-staging is not a finalized Linux/macOS export. Output is placed in workspace `Builds/Godot/<rid>/` and contains Original and Everest launchers. macOS exports are ad-hoc signed after content publication; distribution signing and notarization use the publisher's identity.

Original contributions are licensed under MPL-2.0; see [LICENSE](LICENSE) and [NOTICE.md](NOTICE.md).
