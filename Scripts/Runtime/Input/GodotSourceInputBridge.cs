using System;
using System.Collections.Generic;
using Celeste.Game.Runtime;
using Celeste.Input.Devices.Keyboard;
using Godot;

// ReSharper disable once CheckNamespace
namespace Celeste.Godot.Runtime.Input
{
    internal sealed class GodotSourceInputBridge : IDisposable
    {
        private readonly GodotSourceInputDevices _devices;
        private readonly GameApplication _process;
        private bool _disposed;
        private readonly HashSet<int> _registeredGamePads = [];
        private string _composition = string.Empty;
        private Vector2I _compositionSelection;

        internal GodotSourceInputBridge(GodotSourceInputDevices devices, GameApplication process)
        {
            _devices = devices;
            _process = process;
            foreach (var id in global::Godot.Input.GetConnectedJoypads())
                if (_registeredGamePads.Add(id))
                    process.FnaGamePads.Platform.AddInstance(id);
            global::Godot.Input.Singleton.JoyConnectionChanged += JoyConnectionChanged;
        }

        internal void PollEvents()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _process.ActualGame.IsActive = _devices.Window.HasFocus();
            if (!_process.ActualGame.IsActive)
            {
                _devices.Keyboard.Clear();
                _devices.MouseTouch.Clear();
                for (var i = _process.KeyboardState.Keys.Count - 1; i >= 0; --i)
                    _process.ActualGame.OnPhysicalKeyUp(_process.KeyboardState, _process.TextBindings,
                        _process.KeyboardState.Keys[i]);
                return;
            }

            if (!_devices.IsTextInputActive()) return;
            var text = DisplayServer.ImeGetText();
            var selection = DisplayServer.ImeGetSelection();
            if (text == _composition && selection == _compositionSelection) return;
            _composition = text;
            _compositionSelection = selection;
            _process.TextInput.OnTextEditing(text, selection.X, selection.Y);
        }

        internal void HandleEvent(InputEvent input)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_devices.Window.HasFocus()) return;
            switch (input)
            {
                case InputEventKey key:
                    var mapped = _devices.Keyboard.ReadKey(key);
                    if (mapped != SourceKeys.None)
                    {
                        if (key.Pressed)
                            _process.ActualGame.OnPhysicalKeyDown(_process.KeyboardState, _process.TextBindings,
                                _process.TextInput, mapped, key.Echo ? (byte)1 : (byte)0);
                        else _process.ActualGame.OnPhysicalKeyUp(_process.KeyboardState, _process.TextBindings, mapped);
                    }

                    // Control text was emitted by the shared key handler. Committed
                    // Unicode scalars from the SDK are delivered as UTF-16 source chars.
                    if (key is { Pressed: true, Unicode: >= 32 } && _devices.IsTextInputActive() &&
                        !_process.ActualGame.SuppressPhysicalTextInput)
                        foreach (var value in char.ConvertFromUtf32(checked((int)key.Unicode)))
                            _process.TextInput.OnTextInput(value);
                    break;
                case InputEventMouseMotion motion:
                    _devices.MouseTouch.Motion(motion.ScreenRelative);
                    break;
                case InputEventMouseButton { Pressed: true } button:
                    if (button.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
                    {
                        // Godot Factor describes physical scroll magnitude, not the source cumulative wheel counter.
                        _wheelRemainder += (button.ButtonIndex == MouseButton.WheelUp ? 1f : -1f) * button.Factor;
                        var ticks = (int)_wheelRemainder;
                        if (ticks != 0)
                        {
                            _process.FnaInput.OnMouseWheel(ticks);
                            _wheelRemainder -= ticks;
                        }
                    }
                    else
                    {
                        byte index = button.ButtonIndex switch
                        {
                            MouseButton.Left => 1, MouseButton.Middle => 2, MouseButton.Right => 3,
                            MouseButton.Xbutton1 => 4, MouseButton.Xbutton2 => 5, _ => 0,
                        };
                        if (index != 0) _process.FnaInput.OnMouseButtonDown(index);
                    }

                    break;
                case InputEventScreenTouch touch:
                    _devices.MouseTouch.Touch(touch.Index, ToWindow(touch.Position),
                        touch is { Pressed: true, Canceled: false });
                    var normalized = Normalize(ToWindow(touch.Position));
                    if (touch is { Pressed: true, Canceled: false })
                        _process.FnaInput.OnFingerDown(touch.Index, normalized.X, normalized.Y);
                    else _process.FnaInput.OnFingerUp(touch.Index, normalized.X, normalized.Y);
                    break;
                case InputEventScreenDrag drag:
                    _devices.MouseTouch.Touch(drag.Index, ToWindow(drag.Position), true);
                    var position = Normalize(ToWindow(drag.Position));
                    var delta = Normalize(drag.ScreenRelative);
                    _process.FnaInput.OnFingerMotion(drag.Index, position.X, position.Y, delta.X, delta.Y);
                    break;
            }
        }

        private float _wheelRemainder;

        private Vector2 ToWindow(Vector2 position)
        {
            return _devices.Window.GetFinalTransform() * position;
        }

        private Vector2 Normalize(Vector2 position)
        {
            return position / new Vector2(Math.Max(1, _devices.Window.Size.X),
                Math.Max(1, _devices.Window.Size.Y));
        }

        private void JoyConnectionChanged(long id, bool connected)
        {
            var deviceId = checked((int)id);
            if (connected)
            {
                if (_registeredGamePads.Add(deviceId)) _process.FnaGamePads.Platform.AddInstance(deviceId);
            }
            else if (_registeredGamePads.Remove(deviceId))
            {
                _process.FnaGamePads.Platform.RemoveInstance(deviceId);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            global::Godot.Input.Singleton.JoyConnectionChanged -= JoyConnectionChanged;
            foreach (var id in _registeredGamePads) _process.FnaGamePads.Platform.RemoveInstance(id);
            _registeredGamePads.Clear();
            _disposed = true;
        }
    }
}
