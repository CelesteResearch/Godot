using System;
using System.Collections.Generic;
using Celeste.Input.Devices.Keyboard;
using Godot;
using Environment = System.Environment;

// ReSharper disable once CheckNamespace
namespace Celeste.Godot.Runtime.Input
{
    internal sealed class GodotSourceKeyboardDevice : ISourceKeyboardDevice
    {
        private readonly Window _window;

        // Physical event state only; the shared KeyboardState remains the gameplay owner.
        private readonly Dictionary<(Key, KeyLocation), SourceKeys> _pressed = new();

        internal GodotSourceKeyboardDevice(Window window)
        {
            _window = window;
        }

        public string GetEnvironmentVariable(string name)
        {
            return Environment.GetEnvironmentVariable(name)!;
        }

        public bool IsWindows => OS.GetName() == "Windows";

        internal SourceKeys ReadKey(InputEventKey input)
        {
            var identity = (input.PhysicalKeycode, input.Location);
            if (!input.Pressed && _pressed.Remove(identity, out var previous)) return previous;
            var value = GodotSourceKeyCodes.ToSource(input.Keycode, input.Location);
            if (input.Pressed && value != SourceKeys.None) _pressed[identity] = value;
            return value;
        }

        internal void Clear()
        {
            _pressed.Clear();
        }

        public bool ReadWin32KeyboardState(Span<byte> keys)
        {
            if (keys.Length < 256) throw new ArgumentException("A 256-byte key view is required.", nameof(keys));
            keys.Clear();
            if (!_window.HasFocus()) return true;
            foreach (var value in _pressed.Values) keys[(int)value] = 0x80;
            keys[16] = (byte)(keys[160] | keys[161]);
            keys[17] = (byte)(keys[162] | keys[163]);
            keys[18] = (byte)(keys[164] | keys[165]);
            return true;
        }

        public SourceKeys GetKeyFromScancode(SourceKeys scancode)
        {
            var physical = GodotSourceKeyCodes.ToGodot(scancode);
            return physical == Key.None
                ? scancode
                : GodotSourceKeyCodes.ToSource(DisplayServer.KeyboardGetKeycodeFromPhysical(physical));
        }
    }
}
