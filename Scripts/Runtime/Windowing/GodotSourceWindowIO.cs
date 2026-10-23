using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Celeste.Core.Geometry;
using Celeste.Graphics.Rendering.Devices;
using Celeste.Platform.Native.Displays;
using Godot;
using Environment = System.Environment;

// ReSharper disable once CheckNamespace
namespace Celeste.Godot.Runtime.Windowing
{
    internal sealed class GodotSourceWindowIO : ISourceHostWindowIO
    {
        private readonly Window _window;
        private readonly int _windowId;
        private readonly bool _headless;
        private readonly bool _editorChildWindow;
        private readonly NativeSourceDisplayIO _displays;
        private readonly SourceGraphicsAdapterProcess _adapters;
        private string? _headlessDisplayName;

        internal GodotSourceWindowIO(Window actualWindow, NativeSourceDisplayIO displays)
        {
            _window = actualWindow;
            _displays = displays;
            _adapters = new(displays);
            _headless = string.Equals(DisplayServer.GetName(), "headless", StringComparison.OrdinalIgnoreCase);
            if (!_headless && _window.IsEmbedded())
                throw new InvalidOperationException("The shared host requires a standalone native Godot window.");
            _windowId = _window.GetWindowId();
            if (_headless)
            {
                _headlessDisplayName = _adapters.DefaultAdapter.DeviceName;
            }
            else
            {
                Handle = new(DisplayServer.WindowGetNativeHandle(
                    DisplayServer.HandleType.WindowHandle, _windowId));
                if (Handle == IntPtr.Zero)
                    throw new InvalidOperationException("An existing native Godot window is required.");
                _editorChildWindow = OperatingSystem.IsWindows() &&
                                     IsEditorChildWindow(Environment.GetCommandLineArgs(), Handle);
            }
        }

        public IntPtr Handle { get; }

        public IntRect Bounds
        {
            get
            {
                GetWindowSize(out var width, out var height);
                if (_headless) return new(_window.Position.X, _window.Position.Y, width, height);
                if (!OperatingSystem.IsWindows())
                {
                    var position = DisplayServer.WindowGetPosition(_windowId);
                    return new(position.X, position.Y, width, height);
                }

                // ReSharper disable once ConvertIfStatementToReturnStatement
                if (!GetWindowRect(Handle, out var outer))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return new(outer.Left, outer.Top, width, height);
            }
        }

        public string DisplayName
        {
            get
            {
                if (_headless) return _headlessDisplayName!;
                if (!OperatingSystem.IsWindows())
                {
                    var screen = DisplayServer.WindowGetCurrentScreen(_windowId);
                    var position = DisplayServer.ScreenGetPosition(screen);
                    var size = DisplayServer.ScreenGetSize(screen);
                    return FindDisplay(position, size);
                }

                var monitor = ReadMonitor(MonitorFromWindow(Handle, 2)).Monitor;
                for (var index = 0; index < _displays.GetNumVideoDisplays(); ++index)
                {
                    _displays.GetDisplayBounds(index, out var bounds);
                    if (bounds.X == monitor.Left && bounds.Y == monitor.Top &&
                        bounds.Width == monitor.Right - monitor.Left &&
                        bounds.Height == monitor.Bottom - monitor.Top)
                        return _adapters.Adapters[index].DeviceName;
                }

                throw new InvalidOperationException("The physical window monitor has no corresponding source display.");
            }
        }

        private string FindDisplay(Vector2I position, Vector2I size)
        {
            for (var index = 0; index < _displays.GetNumVideoDisplays(); ++index)
            {
                _displays.GetDisplayBounds(index, out var bounds);
                if (bounds.X == position.X && bounds.Y == position.Y &&
                    bounds.Width == size.X && bounds.Height == size.Y)
                    return _adapters.Adapters[index].DeviceName;
            }

            throw new InvalidOperationException("The Godot screen has no corresponding source display.");
        }

        public bool Resizable
        {
            get => !_window.Unresizable;
            set => _window.Unresizable = !value;
        }

        public bool Borderless
        {
            get => _window.Borderless;
            set => _window.Borderless = value;
        }

        public void SetTitle(string title)
        {
            _window.Title = title;
        }

        public void ApplyResolution(int width, int height, bool fullscreen, string displayName)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Client dimensions must be positive.");
            if (_editorChildWindow)
            {
                // The editor owns its child window's position, size, and windowed mode.
                // ReSharper disable once LoopCanBeConvertedToQuery
                foreach (var adapter in _adapters.Adapters)
                    if (adapter.DeviceName == displayName)
                        return;
                throw new ArgumentException("The requested physical display does not exist.", nameof(displayName));
            }

            if (_headless)
            {
                foreach (var adapter in _adapters.Adapters)
                {
                    if (adapter.DeviceName != displayName) continue;
                    _headlessDisplayName = adapter.DeviceName;
                    _window.Size = new(width, height);
                    return;
                }

                throw new ArgumentException("The requested physical display does not exist.", nameof(displayName));
            }

            var displayChanged = DisplayName != displayName;
            var sizeChanged = _window.Size != new Vector2I(width, height);
            var wasFullscreen = _window.Mode is Window.ModeEnum.Fullscreen or Window.ModeEnum.ExclusiveFullscreen;
            if (displayChanged)
            {
                _window.Mode = Window.ModeEnum.Windowed;
                MoveToDisplay(displayName);
            }

            _window.Mode = fullscreen ? Window.ModeEnum.Fullscreen : Window.ModeEnum.Windowed;
            if (fullscreen) return;
            _window.Size = new(width, height);
            if (displayChanged || sizeChanged || wasFullscreen)
                _window.MoveToCenter();
        }

        public void GetWindowSize(out int width, out int height)
        {
            if (_editorChildWindow)
            {
                if (!GetClientRect(Handle, out var client))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                width = client.Right - client.Left;
                height = client.Bottom - client.Top;
                return;
            }

            var size = _headless ? _window.Size : DisplayServer.WindowGetSize(_windowId);
            width = size.X;
            height = size.Y;
        }

        // Godot's native window dimensions are physical framebuffer pixels, not the scaled game viewport.
        public void GetDrawableSize(out int width, out int height)
        {
            GetWindowSize(out width, out height);
        }

        public void LogWarning(string message)
        {
            GD.PushWarning(message);
        }

        private static bool IsEditorChildWindow(string[] arguments, IntPtr actualWindow)
        {
            if (!TryReadEditorParent(arguments, out var expectedParent, out var expectedProcess)) return false;
            var actualParent = GetWindow(actualWindow, 4);
            if (actualParent == IntPtr.Zero) return false;
            _ = GetWindowThreadProcessId(actualParent, out var actualProcess);
            return MatchesEditorParent(expectedParent, expectedProcess,
                unchecked((ulong)actualParent.ToInt64()), actualProcess, GetWindowLongPtr(actualWindow, -16).ToInt64());
        }

        private static bool MatchesEditorParent(ulong expectedParent, uint expectedProcess,
            ulong actualParent, uint actualProcess, long style)
        {
            return expectedParent != 0 && expectedProcess != 0 && actualParent == expectedParent &&
                   actualProcess == expectedProcess && (style & 0x80000000L) != 0 && (style & 0x40000000L) == 0;
        }

        private static bool TryReadEditorParent(string[] arguments, out ulong parent, out uint process)
        {
            parent = 0;
            process = 0;
            var hasParent = false;
            var hasProcess = false;
            for (var i = 0; i < arguments.Length; i++)
            {
                var argument = arguments[i];
                if (argument is "--" or "++") break;
                switch (argument)
                {
                    case "--wid":
                        if (hasParent || i + 1 == arguments.Length ||
                            !ulong.TryParse(arguments[++i], NumberStyles.None, CultureInfo.InvariantCulture,
                                out parent) ||
                            parent == 0) return false;
                        hasParent = true;

                        break;
                    case "--editor-pid":
                        if (hasProcess || i + 1 == arguments.Length ||
                            !uint.TryParse(arguments[++i], NumberStyles.None, CultureInfo.InvariantCulture,
                                out process) ||
                            process == 0) return false;
                        hasProcess = true;

                        break;
                }
            }

            return hasParent && hasProcess;
        }

        private void MoveToDisplay(string displayName)
        {
            var displayIndex = -1;
            for (var index = 0; index < _adapters.Adapters.Count; ++index)
                if (_adapters.Adapters[index].DeviceName == displayName)
                {
                    displayIndex = index;
                    break;
                }

            if (displayIndex < 0)
                throw new ArgumentException("The requested source display does not exist.", nameof(displayName));
            _displays.GetDisplayBounds(displayIndex, out var target);
            var origin = ReadScreensOrigin();
            for (var screen = 0; screen < DisplayServer.GetScreenCount(); ++screen)
            {
                var position = DisplayServer.ScreenGetPosition(screen) + origin;
                var size = DisplayServer.ScreenGetSize(screen);
                if (position.X != target.X || position.Y != target.Y ||
                    size.X != target.Width || size.Y != target.Height)
                    continue;
                DisplayServer.WindowSetCurrentScreen(screen, _windowId);
                return;
            }

            throw new InvalidOperationException("The source display has no corresponding Godot screen.");
        }

        private static Vector2I ReadScreensOrigin()
        {
            var origin = Vector2I.Zero;
            if (!OperatingSystem.IsWindows()) return origin;
            // ReSharper disable once ConvertIfStatementToReturnStatement
            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
                {
                    var bounds = ReadMonitor(monitor).Monitor;
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
            // ReSharper disable once ConvertIfStatementToReturnStatement
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

#pragma warning disable SYSLIB1054
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);
#pragma warning restore SYSLIB1054

#pragma warning disable SYSLIB1054
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
#pragma warning restore SYSLIB1054

#pragma warning disable SYSLIB1054
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
#pragma warning restore SYSLIB1054

#pragma warning disable SYSLIB1054
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
#pragma warning restore SYSLIB1054

#pragma warning disable SYSLIB1054
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);
#pragma warning restore SYSLIB1054

#pragma warning disable SYSLIB1054
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rect);
#pragma warning restore SYSLIB1054

#pragma warning disable SYSLIB1054
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
#pragma warning restore SYSLIB1054

#pragma warning disable SYSLIB1054
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr rect, MonitorCallback callback,
            IntPtr data);
#pragma warning restore SYSLIB1054
    }
}
