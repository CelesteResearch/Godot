using System;
using System.Collections.Generic;
using Celeste.Graphics.Rendering.Devices;
using Celeste.Input.Devices.Mouse;
using Godot;

namespace Celeste.Godot.Runtime.Input
{
    internal sealed class GodotSourceMouseTouchDevice : ISourceSDLMouseTouchDevice
    {
        private readonly Window window;
        private readonly ISourceHostWindowIO host;
        private Vector2 relative;
        private readonly SortedDictionary<int, Vector2> touches = new();

        internal GodotSourceMouseTouchDevice(Window window, ISourceHostWindowIO host)
        {
            this.window = window;
            this.host = host;
        }

        internal void Motion(Vector2 delta) => relative += delta;

        internal void Touch(int id, Vector2 position, bool pressed)
        {
            if (pressed) touches[id] = position;
            else touches.Remove(id);
        }

        internal void Clear()
        {
            relative = Vector2.Zero;
            touches.Clear();
        }

        private uint Buttons => !window.HasFocus()
            ? 0
            : (global::Godot.Input.IsMouseButtonPressed(MouseButton.Left) ? 1u : 0u) |
              (global::Godot.Input.IsMouseButtonPressed(MouseButton.Middle) ? 2u : 0u) |
              (global::Godot.Input.IsMouseButtonPressed(MouseButton.Right) ? 4u : 0u) |
              (global::Godot.Input.IsMouseButtonPressed(MouseButton.Xbutton1) ? 8u : 0u) |
              (global::Godot.Input.IsMouseButtonPressed(MouseButton.Xbutton2) ? 16u : 0u);

        public uint GetMouseState(out int x, out int y)
        {
            Vector2I position = DisplayServer.MouseGetPosition() -
                                DisplayServer.WindowGetPosition(window.GetWindowId());
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
            x = (int)relative.X;
            y = (int)relative.Y;
            relative -= new Vector2(x, y);
            return Buttons;
        }

        public int GetRelativeMouseMode() =>
            global::Godot.Input.MouseMode == global::Godot.Input.MouseModeEnum.Captured ? 1 : 0;

        public int SetRelativeMouseMode(int enabled)
        {
            if (enabled != 0 && !window.HasFocus()) return -1;
            global::Godot.Input.MouseMode = enabled != 0
                ? global::Godot.Input.MouseModeEnum.Captured
                : global::Godot.Input.MouseModeEnum.Visible;
            return GetRelativeMouseMode() == (enabled != 0 ? 1 : 0) ? 0 : -1;
        }

        private void RequireWindow(IntPtr handle)
        {
            if (handle != host.Handle)
                throw new ArgumentException("The input window must be the shared host window.", nameof(handle));
        }

        public void GetWindowPosition(IntPtr handle, out int x, out int y)
        {
            RequireWindow(handle);
            var p = DisplayServer.WindowGetPosition(window.GetWindowId());
            x = p.X;
            y = p.Y;
        }

        public void WarpMouseInWindow(IntPtr handle, int x, int y)
        {
            RequireWindow(handle);
            if (window.HasFocus())
                DisplayServer.WarpMouse(new Vector2I(x, y));
        }

        // Godot exposes touch indexes rather than OS touchscreen devices. One physical
        // input surface owns all indexes delivered to this Window.
        public int GetNumTouchDevices() => DisplayServer.IsTouchscreenAvailable() ? 1 : 0;
        public long GetTouchDevice(int index) => index == 0 && GetNumTouchDevices() != 0 ? 1 : 0;
        public int GetNumTouchFingers(long device) => device == 1 && window.HasFocus() ? touches.Count : 0;

        public IntPtr GetTouchFinger(long device, int index)
        {
            if (device != 1 || !window.HasFocus()) return IntPtr.Zero;
            foreach (int id in touches.Keys)
                if (index-- == 0)
                    return new IntPtr(id + 1);
            return IntPtr.Zero;
        }

        public long ReadFingerId(IntPtr finger) => finger.ToInt64() - 1;

        public float ReadFingerX(IntPtr finger) =>
            touches[checked((int)ReadFingerId(finger))].X / Math.Max(1, window.Size.X);

        public float ReadFingerY(IntPtr finger) =>
            touches[checked((int)ReadFingerId(finger))].Y / Math.Max(1, window.Size.Y);
    }
}
