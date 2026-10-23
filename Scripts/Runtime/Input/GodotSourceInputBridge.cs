using System;
using System.Collections.Generic;
using Celeste.Game.Runtime;
using Celeste.Input.Devices.Keyboard;
using Godot;

namespace Celeste.Godot.Runtime.Input
{
    internal sealed class GodotSourceInputBridge : IDisposable
    {
        private readonly GodotSourceInputDevices devices;
        private readonly GameApplication process;
        private bool disposed;
        private readonly HashSet<int> registeredGamePads = new();
        private string composition = string.Empty;
        private Vector2I compositionSelection;

        internal GodotSourceInputBridge(GodotSourceInputDevices devices, GameApplication process)
        {
            this.devices = devices;
            this.process = process;
            foreach (int id in global::Godot.Input.GetConnectedJoypads())
                if (registeredGamePads.Add(id))
                    process.FnaGamePads.Platform.AddInstance(id);
            global::Godot.Input.Singleton.JoyConnectionChanged += JoyConnectionChanged;
        }

        internal void PollEvents()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            process.ActualGame.IsActive = devices.Window.HasFocus();
            if (!process.ActualGame.IsActive)
            {
                devices.Keyboard.Clear();
                devices.MouseTouch.Clear();
                for (int i = process.KeyboardState.Keys.Count - 1; i >= 0; --i)
                    process.ActualGame.OnPhysicalKeyUp(process.KeyboardState, process.TextBindings,
                        process.KeyboardState.Keys[i]);
                return;
            }

            if (devices.IsTextInputActive())
            {
                string text = DisplayServer.ImeGetText();
                Vector2I selection = DisplayServer.ImeGetSelection();
                if (text != composition || selection != compositionSelection)
                {
                    composition = text;
                    compositionSelection = selection;
                    process.TextInput.OnTextEditing(text, selection.X, selection.Y);
                }
            }
        }

        internal void HandleEvent(InputEvent input)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!devices.Window.HasFocus()) return;
            switch (input)
            {
                case InputEventKey key:
                    SourceKeys mapped = devices.Keyboard.ReadKey(key);
                    if (mapped != SourceKeys.None)
                    {
                        if (key.Pressed)
                            process.ActualGame.OnPhysicalKeyDown(process.KeyboardState, process.TextBindings,
                                process.TextInput, mapped, key.Echo ? (byte)1 : (byte)0);
                        else process.ActualGame.OnPhysicalKeyUp(process.KeyboardState, process.TextBindings, mapped);
                    }

                    // Control text was emitted by the shared key handler. Committed
                    // Unicode scalars from the SDK are delivered as UTF-16 source chars.
                    if (key.Pressed && key.Unicode >= 32 && devices.IsTextInputActive() &&
                        !process.ActualGame.SuppressPhysicalTextInput)
                        foreach (char value in char.ConvertFromUtf32(checked((int)key.Unicode)))
                            process.TextInput.OnTextInput(value);
                    break;
                case InputEventMouseMotion motion:
                    devices.MouseTouch.Motion(motion.ScreenRelative);
                    break;
                case InputEventMouseButton button when button.Pressed:
                    if (button.ButtonIndex == MouseButton.WheelUp || button.ButtonIndex == MouseButton.WheelDown)
                    {
                        // Godot Factor describes physical scroll magnitude, not the source cumulative wheel counter.
                        wheelRemainder += (button.ButtonIndex == MouseButton.WheelUp ? 1f : -1f) * button.Factor;
                        int ticks = (int)wheelRemainder;
                        if (ticks != 0)
                        {
                            process.FnaInput.OnMouseWheel(ticks);
                            wheelRemainder -= ticks;
                        }
                    }
                    else
                    {
                        byte index = button.ButtonIndex switch
                        {
                            MouseButton.Left => 1, MouseButton.Middle => 2, MouseButton.Right => 3,
                            MouseButton.Xbutton1 => 4, MouseButton.Xbutton2 => 5, _ => 0
                        };
                        if (index != 0) process.FnaInput.OnMouseButtonDown(index);
                    }

                    break;
                case InputEventScreenTouch touch:
                    devices.MouseTouch.Touch(touch.Index, ToWindow(touch.Position), touch.Pressed && !touch.Canceled);
                    Vector2 normalized = Normalize(ToWindow(touch.Position));
                    if (touch.Pressed && !touch.Canceled)
                        process.FnaInput.OnFingerDown(touch.Index, normalized.X, normalized.Y);
                    else process.FnaInput.OnFingerUp(touch.Index, normalized.X, normalized.Y);
                    break;
                case InputEventScreenDrag drag:
                    devices.MouseTouch.Touch(drag.Index, ToWindow(drag.Position), true);
                    Vector2 position = Normalize(ToWindow(drag.Position));
                    Vector2 delta = Normalize(drag.ScreenRelative);
                    process.FnaInput.OnFingerMotion(drag.Index, position.X, position.Y, delta.X, delta.Y);
                    break;
            }
        }

        private float wheelRemainder;
        private Vector2 ToWindow(Vector2 position) => devices.Window.GetFinalTransform() * position;

        private Vector2 Normalize(Vector2 position) => position / new Vector2(Math.Max(1, devices.Window.Size.X),
            Math.Max(1, devices.Window.Size.Y));

        private void JoyConnectionChanged(long id, bool connected)
        {
            int deviceId = checked((int)id);
            if (connected)
            {
                if (registeredGamePads.Add(deviceId)) process.FnaGamePads.Platform.AddInstance(deviceId);
            }
            else if (registeredGamePads.Remove(deviceId)) process.FnaGamePads.Platform.RemoveInstance(deviceId);
        }

        public void Dispose()
        {
            if (disposed) return;
            global::Godot.Input.Singleton.JoyConnectionChanged -= JoyConnectionChanged;
            foreach (int id in registeredGamePads) process.FnaGamePads.Platform.RemoveInstance(id);
            registeredGamePads.Clear();
            disposed = true;
        }
    }
}
