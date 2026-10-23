using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Celeste.Core.Framework.Startup;
using Celeste.Framework.Startup;
using Celeste.Game.Execution.Diagnostics;
using Celeste.Game.Runtime.Physical.Runtime;
using Celeste.Graphics.Rendering.Devices;
using Godot;
using Celeste.Godot.Runtime.Interop;

namespace Celeste.Godot.Runtime.Startup
{
    internal sealed class GodotSourceRuntimeIO : ManagedSourceRuntimeIO, ISourceGameHostRuntimeIO,
        ISourceRuntimeErrorDevice, ISourceWindowFocusDevice, ISourceWindowFocusIO,
        ISourcePhysicalModulePathIO, IOriginalErrorLogIo
    {
        private readonly bool headless;
        private readonly ISourceHostWindowIO physicalWindow;
        private readonly Func<bool> touchDeviceConnected;
        private readonly Action<string, string> setHint;

        internal GodotSourceRuntimeIO(ISourceHostWindowIO physicalWindow,
            string physicalOwnerAssemblyPath, string applicationBaseDirectory, string[] arguments,
            MethodInfo externalHostStartupFrame, Func<bool> touchDeviceConnected, Action<string, string> setHint)
            : base(physicalOwnerAssemblyPath, applicationBaseDirectory, arguments, externalHostStartupFrame)
        {
            headless = string.Equals(DisplayServer.GetName(), "headless", StringComparison.OrdinalIgnoreCase);
            this.physicalWindow = physicalWindow ?? throw new ArgumentNullException(nameof(physicalWindow));
            this.touchDeviceConnected =
                touchDeviceConnected ?? throw new ArgumentNullException(nameof(touchDeviceConnected));
            this.setHint = setHint ?? throw new ArgumentNullException(nameof(setHint));
        }

        void IOriginalErrorLogIo.OpenDocument(string path)
        {
            if (headless)
            {
                Console.Error.WriteLine("Celeste error log: " + System.IO.Path.GetFullPath(path));
                return;
            }

            base.OpenDocument(path);
        }

        public Action<IntPtr> SdlRaiseWindow => null!;

        public bool GetTouchDeviceConnected()
        {
            return touchDeviceConnected();
        }

        public bool TryGetPhysicalModulePath(Module actualModule, out string physicalPath)
        {
            ArgumentNullException.ThrowIfNull(actualModule);
            // A manifest assembly in Godot's project load context has a deployed PE
            // even when the CLR Location is empty. Relinked mod contexts use the
            // separate Everest cache path after this physical-host lookup declines.
            if (!ReferenceEquals(actualModule, actualModule.Assembly.ManifestModule) ||
                AssemblyLoadContext.GetLoadContext(actualModule.Assembly)?.GetType().FullName !=
                "GodotPlugins.PluginLoadContext")
            {
                physicalPath = null!;
                return false;
            }

            physicalPath = GodotAssemblyPathIO.GetPhysicalPath(actualModule.Assembly);
            return true;
        }

        public void SetMouseVisible(bool visible)
        {
            global::Godot.Input.MouseMode =
                visible ? global::Godot.Input.MouseModeEnum.Visible : global::Godot.Input.MouseModeEnum.Hidden;
        }

        public void ShowRuntimeError(string title, string message)
        {
            GD.PushError(title + "\n" + message);
        }

        public void SetHint(string name, string value)
        {
            setHint(name, value);
        }

        public void RaiseWindow(IntPtr handle)
        {
            SetForegroundWindow(handle);
        }

        public bool SetForegroundWindow(IntPtr handle)
        {
            RequireWindow(handle);
            if (headless || handle == IntPtr.Zero) return false;
            if (OperatingSystem.IsWindows())
                return IsWindowVisible(handle) && NativeSetForegroundWindow(handle);
            DisplayServer.WindowMoveToForeground();
            return DisplayServer.WindowIsFocused();
        }

        private void RequireWindow(IntPtr handle)
        {
            if (handle != physicalWindow.Handle)
            {
                throw new ArgumentException("Only the borrowed Godot game window can be focused.", nameof(handle));
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeSetForegroundWindow(IntPtr window);
    }
}
