using System;

namespace Celeste.Godot.Runtime.Graphics
{
    internal static class GodotGraphicsBackendSelection
    {
        internal static GodotGraphicsBackend Resolve(GodotGraphicsBackend configured, string[] arguments)
        {
            foreach (string argument in arguments)
            {
                const string prefix = "--graphics-backend=";
                if (!argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                configured = argument.Substring(prefix.Length).ToLowerInvariant() switch
                {
                    "auto" => GodotGraphicsBackend.Auto,
                    "d3d11" => GodotGraphicsBackend.Direct3D11,
                    "d3d12" => GodotGraphicsBackend.Direct3D12,
                    "vulkan" => GodotGraphicsBackend.Vulkan,
                    _ => throw new ArgumentException(
                        "Supported Godot graphics backends are auto, d3d11, vulkan and d3d12.")
                };
            }

            if (configured == GodotGraphicsBackend.Auto)
                return OperatingSystem.IsWindows() ? GodotGraphicsBackend.Direct3D11 : GodotGraphicsBackend.Vulkan;
            if (!OperatingSystem.IsWindows() && configured != GodotGraphicsBackend.Vulkan)
                throw new PlatformNotSupportedException("Direct3D backends require Windows; select Vulkan or Auto.");
            return configured;
        }
    }
}
