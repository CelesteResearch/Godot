using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Celeste.Platform.Native.Displays;
using Celeste.Graphics.Direct3D11.Submission;
using Celeste.Graphics.Direct3D12.Submission;
using Celeste.Graphics.Vulkan.Submission;
using Celeste.Graphics.Effects;
using Celeste.Audio.Fmod.Runtime;

namespace Celeste.Godot.Runtime.Interop
{
    internal sealed class GodotNativeLibraryResolver : IDisposable
    {
        private readonly string directory;
        private readonly object gate = new();
        private readonly Dictionary<string, IntPtr> handles = new(StringComparer.OrdinalIgnoreCase);
        private bool bound;
        private bool disposed;

        internal GodotNativeLibraryResolver(string nativeDirectory)
        {
            directory = Path.GetFullPath(nativeDirectory);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        }

        internal static string RuntimeIdentifier
        {
            get
            {
                string platform = OperatingSystem.IsWindows() ? "win"
                    : OperatingSystem.IsLinux() ? "linux"
                    : OperatingSystem.IsMacOS() ? "osx"
                    : throw new PlatformNotSupportedException("The native host supports Windows, Linux and macOS.");
                string architecture = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => "x64",
                    Architecture.Arm64 => "arm64",
                    _ => throw new PlatformNotSupportedException("The native host requires x64 or arm64.")
                };
                return platform + "-" + architecture;
            }
        }

        internal void Bind()
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (bound) return;
                // These are the actual managed import owners, in the same Godot load
                // context as the shared runtime. Never register the SDK GodotSharp assembly.
                NativeLibrary.SetDllImportResolver(typeof(NativeSourceDisplayIO).Assembly, Resolve);
                NativeLibrary.SetDllImportResolver(typeof(Direct3D11GraphicsPackets).Assembly, Resolve);
                NativeLibrary.SetDllImportResolver(typeof(Win32VulkanGraphicsSubmission).Assembly, Resolve);
                NativeLibrary.SetDllImportResolver(typeof(Win32Direct3D12GraphicsSubmission).Assembly, Resolve);
                NativeLibrary.SetDllImportResolver(typeof(CpuEffectFrontend).Assembly, Resolve);
                NativeLibrary.SetDllImportResolver(typeof(FmodStudioRuntime).Assembly, Resolve);
                NativeLibrary.SetDllImportResolver(typeof(KeraLua.Lua).Assembly, Resolve);
                // The physical Godot graphics adapter also declares native imports.
                NativeLibrary.SetDllImportResolver(typeof(GodotNativeLibraryResolver).Assembly, Resolve);
                bound = true;
            }
        }

        private IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
        {
            string? file = name switch
            {
                "SDL2" or "SDL2.dll" => "SDL2.dll",
                "FNA3D" or "FNA3D.dll" => "FNA3D.dll",
                "FAudio" or "FAudio.dll" => "FAudio.dll",
                "libtheorafile" or "libtheorafile.dll" => "libtheorafile.dll",
                "fmod64" or "fmod64.dll" => "fmod64.dll",
                "fmod" or "fmod.dll" => "fmod.dll",
                "fmodstudio" or "fmodstudio.dll" => "fmodstudio.dll",
                "fmod_SDL" or "fmod_SDL.dll" => "fmod_SDL.dll",
                "lua54" or "lua54.dll" => "lua54.dll",
                "CelesteGraphicsD3D11" or "CelesteGraphicsD3D11.dll" => "CelesteGraphicsD3D11.dll",
                "CelesteGraphicsEffects" or "CelesteGraphicsEffects.dll" => "CelesteGraphicsEffects.dll",
                "CelesteGraphicsDirect3D12" or "CelesteGraphicsDirect3D12.dll" => "CelesteGraphicsDirect3D12.dll",
                "CelesteGraphicsVulkan" or "CelesteGraphicsVulkan.dll" => "CelesteGraphicsVulkan.dll",
                _ => null
            };
            if (file != null && !OperatingSystem.IsWindows())
            {
                string stem = file.Substring(0, file.Length - 4);
                if (stem == "fmod64") stem = "fmod";
                file = (stem.StartsWith("lib", StringComparison.Ordinal) ? string.Empty : "lib") + stem +
                       (OperatingSystem.IsMacOS() ? ".dylib" : ".so");
            }

            // OS libraries keep the default platform resolver. A known shared native
            // dependency cannot silently fall back to the Godot executable directory.
            if (file == null) return IntPtr.Zero;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (handles.TryGetValue(file, out IntPtr handle)) return handle;
                string path = Path.Combine(directory, file);
                if (!File.Exists(path))
                    throw new DllNotFoundException($"Shared native dependency is not deployed: {path}");
                handle = NativeLibrary.Load(path, assembly, OperatingSystem.IsWindows()
                    ? DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories
                    : null);
                handles.Add(file, handle);
                return handle;
            }
        }

        // Call only after GameApplication, FMOD, graphics and Lua have been disposed.
        // .NET import resolvers and cached native entry points cannot be reset: this
        // resolver belongs to the one shared-runtime lifetime in this process.
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                foreach (IntPtr handle in handles.Values) NativeLibrary.Free(handle);
                handles.Clear();
            }
        }
    }
}
