using System;
using System.Collections.Generic;
using Celeste.Graphics.Rendering.Devices;
using Celeste.Input.Devices.Mouse;
using Godot;

// ReSharper disable once CheckNamespace
namespace Celeste.Godot.Runtime.Input
{
    internal sealed class GodotSourceMouseTouchDevice : ISourceSDLMouseTouchDevice
    {
        private readonly Window _window;
        private readonly ISourceHostWindowIO _host;
        private Vector2 _relative;
        private readonly SortedDictionary<int, Vector2> _touches = new();

        internal GodotSourceMouseTouchDevice(Window window, ISourceHostWindowIO host)
        {
            _window = window;
            _host = host;
        }

        internal void Motion(Vector2 delta)
        {
            _relative += delta;
        }

        internal void Touch(int id, Vector2 position, bool pressed)
        {
            if (pressed) _touches[id] = position;
            else _touches.Remove(id);
        }

        internal void Clear()
        {
            _relative = Vector2.Zero;
            _touches.Clear();
        }

        private uint Buttons => !_window.HasFocus()
            ? 0
            : (global::Godot.Input.IsMouseButtonPressed(MouseButton.Left) ? 1u : 0u) |
              (global::Godot.Input.IsMouseButtonPressed(MouseButton.Middle) ? 2u : 0u) |
              (global::Godot.Input.IsMouseButtonPressed(MouseButton.Right) ? 4u : 0u) |
              (global::Godot.Input.IsMouseButtonPressed(MouseButton.Xbutton1) ? 8u : 0u) |
              (global::Godot.Input.IsMouseButtonPressed(MouseButton.Xbutton2) ? 16u : 0u);

        public uint GetMouseState(out int x, out int y)
        {
            var position = DisplayServer.MouseGetPosition() -
                           DisplayServer.WindowGetPosition(_window.GetWindowId());
            x = position.X;
            y = position.Y;
            return Buttons;
        }

        public uint GetGlobalMouseState(out int x, out int y)
        {
            var p = DisplayServer.MouseGetPosition();
            x = p.X;
            y = p.Y;
            return Buttons;
        }

        public uint GetRelativeMouseState(out int x, out int y)
        {
            x = (int)_relative.X;
            y = (int)_relative.Y;
            _relative -= new Vector2(x, y);
            return Buttons;
        }

        public int GetRelativeMouseMode()
        {
            return global::Godot.Input.MouseMode == global::Godot.Input.MouseModeEnum.Captured ? 1 : 0;
        }

        public int SetRelativeMouseMode(int enabled)
        {
            if (enabled != 0 && !_window.HasFocus()) return -1;
            global::Godot.Input.MouseMode = enabled != 0
                ? global::Godot.Input.MouseModeEnum.Captured
                : global::Godot.Input.MouseModeEnum.Visible;
            return GetRelativeMouseMode() == (enabled != 0 ? 1 : 0) ? 0 : -1;
        }

        private void RequireWindow(IntPtr handle)
        {
            if (handle != _host.Handle)
                throw new ArgumentException("The input window must be the shared host window.", nameof(handle));
        }

        public void GetWindowPosition(IntPtr handle, out int x, out int y)
        {
            RequireWindow(handle);
            var p = DisplayServer.WindowGetPosition(_window.GetWindowId());
            x = p.X;
            y = p.Y;
        }

        public void WarpMouseInWindow(IntPtr handle, int x, int y)
        {
            RequireWindow(handle);
            if (_window.HasFocus())
                DisplayServer.WarpMouse(new(x, y));
        }

        // Godot exposes touch indexes rather than OS touchscreen devices. One physical
        // input surface owns all indexes delivered to this Window.
        public int GetNumTouchDevices()
        {
            return DisplayServer.IsTouchscreenAvailable() ? 1 : 0;
        }

        public long GetTouchDevice(int index)
        {
            return index == 0 && GetNumTouchDevices() != 0 ? 1 : 0;
        }

        public int GetNumTouchFingers(long device)
        {
            return device == 1 && _window.HasFocus() ? _touches.Count : 0;
        }

        public IntPtr GetTouchFinger(long device, int index)
        {
            if (device != 1 || !_window.HasFocus()) return IntPtr.Zero;
            // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
            foreach (var id in _touches.Keys)
                if (index-- == 0)
                    return new(id + 1);
            return IntPtr.Zero;
        }

        public long ReadFingerId(IntPtr finger)
        {
            return finger.ToInt64() - 1;
        }

        public float ReadFingerX(IntPtr finger)
        {
            return _touches[checked((int)ReadFingerId(finger))].X / Math.Max(1, _window.Size.X);
        }

        public float ReadFingerY(IntPtr finger)
        {
            return _touches[checked((int)ReadFingerId(finger))].Y / Math.Max(1, _window.Size.Y);
        }
    }
}
