using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Celeste.Core.Geometry;
using Celeste.Graphics.Rendering.Devices;
using Celeste.Platform.Native.Displays;
using Godot;

namespace Celeste.Godot.Runtime.Windowing
{
    internal sealed class GodotSourceWindowIO : ISourceHostWindowIO
    {
        private readonly Window window;
        private readonly int windowId;
        private readonly IntPtr nativeHandle;
        private readonly bool headless;
        private readonly bool editorChildWindow;
        private readonly NativeSourceDisplayIO displays;
        private readonly SourceGraphicsAdapterProcess adapters;
        private string? headlessDisplayName;

        internal GodotSourceWindowIO(Window actualWindow, NativeSourceDisplayIO displays)
        {
            window = actualWindow;
            this.displays = displays;
            adapters = new SourceGraphicsAdapterProcess(displays);
            headless = string.Equals(DisplayServer.GetName(), "headless", StringComparison.OrdinalIgnoreCase);
            if (!headless && window.IsEmbedded())
                throw new InvalidOperationException("The shared host requires a standalone native Godot window.");
            windowId = window.GetWindowId();
            if (headless)
            {
                headlessDisplayName = adapters.DefaultAdapter.DeviceName;
            }
            else
            {
                nativeHandle = new IntPtr(DisplayServer.WindowGetNativeHandle(
                    DisplayServer.HandleType.WindowHandle, windowId));
                if (Handle == IntPtr.Zero)
                    throw new InvalidOperationException("An existing native Godot window is required.");
                editorChildWindow = OperatingSystem.IsWindows() &&
                                    IsEditorChildWindow(System.Environment.GetCommandLineArgs(), nativeHandle);
            }
        }

        public IntPtr Handle => nativeHandle;

        public IntRect Bounds
        {
            get
            {
                GetWindowSize(out int width, out int height);
                if (headless) return new IntRect(window.Position.X, window.Position.Y, width, height);
                if (!OperatingSystem.IsWindows())
                {
                    Vector2I position = DisplayServer.WindowGetPosition(windowId);
                    return new IntRect(position.X, position.Y, width, height);
                }

                if (!GetWindowRect(Handle, out NativeRect outer))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return new IntRect(outer.Left, outer.Top, width, height);
            }
        }

        public string DisplayName
        {
            get
            {
                if (headless) return headlessDisplayName!;
                if (!OperatingSystem.IsWindows())
                {
                    int screen = DisplayServer.WindowGetCurrentScreen(windowId);
                    Vector2I position = DisplayServer.ScreenGetPosition(screen);
                    Vector2I size = DisplayServer.ScreenGetSize(screen);
                    return FindDisplay(position, size);
                }

                NativeRect monitor = ReadMonitor(MonitorFromWindow(Handle, 2)).Monitor;
                for (int index = 0; index < displays.GetNumVideoDisplays(); ++index)
                {
                    displays.GetDisplayBounds(index, out IntRect bounds);
                    if (bounds.X == monitor.Left && bounds.Y == monitor.Top &&
                        bounds.Width == monitor.Right - monitor.Left &&
                        bounds.Height == monitor.Bottom - monitor.Top)
                        return adapters.Adapters[index].DeviceName;
                }

                throw new InvalidOperationException("The physical window monitor has no corresponding source display.");
            }
        }

        private string FindDisplay(Vector2I position, Vector2I size)
        {
            for (int index = 0; index < displays.GetNumVideoDisplays(); ++index)
            {
                displays.GetDisplayBounds(index, out IntRect bounds);
                if (bounds.X == position.X && bounds.Y == position.Y &&
                    bounds.Width == size.X && bounds.Height == size.Y)
                    return adapters.Adapters[index].DeviceName;
            }

            throw new InvalidOperationException("The Godot screen has no corresponding source display.");
        }

        public bool Resizable
        {
            get => !window.Unresizable;
            set => window.Unresizable = !value;
        }

        public bool Borderless
        {
            get => window.Borderless;
            set => window.Borderless = value;
        }

        public void SetTitle(string title) => window.Title = title;

        public void ApplyResolution(int width, int height, bool fullscreen, string displayName)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Client dimensions must be positive.");
            if (editorChildWindow)
            {
                // The editor owns its child window's position, size, and windowed mode.
                foreach (SourceGraphicsAdapter adapter in adapters.Adapters)
                    if (adapter.DeviceName == displayName)
                        return;
                throw new ArgumentException("The requested physical display does not exist.", nameof(displayName));
            }

            if (headless)
            {
                foreach (SourceGraphicsAdapter adapter in adapters.Adapters)
                {
                    if (adapter.DeviceName != displayName) continue;
                    headlessDisplayName = adapter.DeviceName;
                    window.Size = new Vector2I(width, height);
                    return;
                }

                throw new ArgumentException("The requested physical display does not exist.", nameof(displayName));
            }

            bool displayChanged = DisplayName != displayName;
            bool sizeChanged = window.Size != new Vector2I(width, height);
            bool wasFullscreen = window.Mode is Window.ModeEnum.Fullscreen or Window.ModeEnum.ExclusiveFullscreen;
            if (displayChanged)
            {
                window.Mode = Window.ModeEnum.Windowed;
                MoveToDisplay(displayName);
            }

            window.Mode = fullscreen ? Window.ModeEnum.Fullscreen : Window.ModeEnum.Windowed;
            if (!fullscreen)
            {
                window.Size = new Vector2I(width, height);
                if (displayChanged || sizeChanged || wasFullscreen)
                    window.MoveToCenter();
            }
        }

        public void GetWindowSize(out int width, out int height)
        {
            if (editorChildWindow)
            {
                if (!GetClientRect(nativeHandle, out NativeRect client))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                width = client.Right - client.Left;
                height = client.Bottom - client.Top;
                return;
            }

            Vector2I size = headless ? window.Size : DisplayServer.WindowGetSize(windowId);
            width = size.X;
            height = size.Y;
        }

        // Godot's native window dimensions are physical framebuffer pixels, not the scaled game viewport.
        public void GetDrawableSize(out int width, out int height) => GetWindowSize(out width, out height);

        public void LogWarning(string message) => GD.PushWarning(message);

        private static bool IsEditorChildWindow(string[] arguments, IntPtr actualWindow)
        {
            if (!TryReadEditorParent(arguments, out ulong expectedParent, out uint expectedProcess)) return false;
            IntPtr actualParent = GetWindow(actualWindow, 4);
            if (actualParent == IntPtr.Zero) return false;
            GetWindowThreadProcessId(actualParent, out uint actualProcess);
            return MatchesEditorParent(expectedParent, expectedProcess,
                unchecked((ulong)actualParent.ToInt64()), actualProcess, GetWindowLongPtr(actualWindow, -16).ToInt64());
        }

        private static bool MatchesEditorParent(ulong expectedParent, uint expectedProcess,
            ulong actualParent, uint actualProcess, long style)
            => expectedParent != 0 && expectedProcess != 0 && actualParent == expectedParent &&
               actualProcess == expectedProcess && (style & 0x80000000L) != 0 && (style & 0x40000000L) == 0;

        private static bool TryReadEditorParent(string[] arguments, out ulong parent, out uint process)
        {
            parent = 0;
            process = 0;
            bool hasParent = false;
            bool hasProcess = false;
            for (int i = 0; i < arguments.Length; i++)
            {
                string argument = arguments[i];
                if (argument == "--" || argument == "++") break;
                if (argument == "--wid")
                {
                    if (hasParent || i + 1 == arguments.Length ||
                        !ulong.TryParse(arguments[++i], NumberStyles.None, CultureInfo.InvariantCulture, out parent) ||
                        parent == 0) return false;
                    hasParent = true;
                }
                else if (argument == "--editor-pid")
                {
                    if (hasProcess || i + 1 == arguments.Length ||
                        !uint.TryParse(arguments[++i], NumberStyles.None, CultureInfo.InvariantCulture, out process) ||
                        process == 0) return false;
                    hasProcess = true;
                }
            }

            return hasParent && hasProcess;
        }

        private void MoveToDisplay(string displayName)
        {
            int displayIndex = -1;
            for (int index = 0; index < adapters.Adapters.Count; ++index)
                if (adapters.Adapters[index].DeviceName == displayName)
                {
                    displayIndex = index;
                    break;
                }

            if (displayIndex < 0)
                throw new ArgumentException("The requested source display does not exist.", nameof(displayName));
            displays.GetDisplayBounds(displayIndex, out IntRect target);
            Vector2I origin = ReadScreensOrigin();
            for (int screen = 0; screen < DisplayServer.GetScreenCount(); ++screen)
            {
                Vector2I position = DisplayServer.ScreenGetPosition(screen) + origin;
                Vector2I size = DisplayServer.ScreenGetSize(screen);
                if (position.X != target.X || position.Y != target.Y ||
                    size.X != target.Width || size.Y != target.Height)
                    continue;
                DisplayServer.WindowSetCurrentScreen(screen, windowId);
                return;
            }

            throw new InvalidOperationException("The source display has no corresponding Godot screen.");
        }

        private static Vector2I ReadScreensOrigin()
        {
            var origin = Vector2I.Zero;
            if (!OperatingSystem.IsWindows()) return origin;
            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
                {
                    NativeRect bounds = ReadMonitor(monitor).Monitor;
                    origin.X = Math.Min(origin.X, bounds.Left);
                    origin.Y = Math.Min(origin.Y, bounds.Top);
                    return true;
                }, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return origin;
        }

        private static MonitorInfo ReadMonitor(IntPtr monitor)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = string.Empty };
            if (!GetMonitorInfo(monitor, ref info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return info;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor, Work;
            public uint Flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string Device;
        }

        private delegate bool MonitorCallback(IntPtr monitor, IntPtr deviceContext, IntPtr rect, IntPtr data);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rect);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr rect, MonitorCallback callback,
            IntPtr data);
    }
}
