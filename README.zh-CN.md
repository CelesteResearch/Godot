[English](README.md) | [简体中文](README.zh-CN.md)

# Godot

基于 Godot 4.5.1 Mono/.NET 的 Windows、Linux 和 macOS 宿主，放在与 Shared、Tools 并列的 `Godot/` 目录。运行素材位于工作区 `Content/`，原生库按 `win-x64`、`linux-x64` 或 `osx-x64` 准备。原版 FMOD 1.10 运行库为 x64，因此 Apple silicon 使用 Rosetta 运行 x64 Godot .NET 编辑器。

按相邻 Tools 仓库 README 使用 `-Targets Godot` 准备工程。需要 PowerShell 7、.NET SDK 10、CMake 3.24+ 和对应平台 C++ 工具链，并提供该平台的原游戏运行库与音频 bank。

导入 `project.godot`，打开 `Scenes/Bootstrap.tscn` 后按 F5。Launch / Source Profile 选择 Original 或 InstalledEverest。Auto 在 Windows 选择 D3D11，在 Linux/macOS 选择 Vulkan；Windows 也可显式选择 Vulkan 或 D3D12。Linux 支持 X11 和 Wayland，macOS Vulkan 使用 MoltenVK。命令行用户参数示例：`-- --everest --graphics-backend=vulkan`。

安装匹配的 .NET 导出模板，运行 `Tools/Export.ps1 -GodotExecutable <Godot Mono 路径>`。可用 `-RuntimeIdentifier win-x64`、`linux-x64` 或 `osx-x64` 选择已准备的目标运行库。Linux 最终导出必须在 Linux 上完成，macOS 最终导出必须在 macOS 上完成，以保留原生库别名的同一模块身份；macOS 还需要为完整应用包签名。Windows 上的交叉暂存不能作为完成的 Linux/macOS 导出。输出位于工作区 `Builds/Godot/<rid>/`，包含 Original 和 Everest 启动入口。macOS 在素材发布完成后执行临时签名；正式分发签名与公证使用发布者身份。
