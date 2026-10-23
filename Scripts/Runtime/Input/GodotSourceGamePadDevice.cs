using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Celeste.Input.Devices.GamePads;
using Godot;

namespace Celeste.Godot.Runtime.Input
{
    internal sealed class GodotSourceGamePadDevice : ISourceSDLGamePadDevice, IDisposable
    {
        private sealed class Loan
        {
            internal int Id;
            internal IntPtr Name;
            internal bool Connected;
            internal bool LinearSdlAxes;
        }

        private readonly Dictionary<IntPtr, Loan> loans = new();
        private readonly Window window;
        private readonly HashSet<int> connected = new();

        internal GodotSourceGamePadDevice(Window window)
        {
            this.window = window;
            global::Godot.Input.Singleton.JoyConnectionChanged += JoyConnectionChanged;
            foreach (int id in global::Godot.Input.GetConnectedJoypads()) connected.Add(id);
        }

        private Loan Borrow(IntPtr handle) => loans.TryGetValue(handle, out Loan? value)
            ? value
            : throw new ArgumentException("Unknown Godot controller loan.", nameof(handle));

        private void JoyConnectionChanged(long id, bool isConnected)
        {
            int deviceId = checked((int)id);
            if (isConnected) connected.Add(deviceId);
            else connected.Remove(deviceId);
            foreach (Loan loan in loans.Values)
                if (loan.Id == deviceId)
                    loan.Connected = isConnected;
        }

        public IntPtr Open(int deviceIndex)
        {
            if (!connected.Contains(deviceIndex)) return IntPtr.Zero;
            var info = global::Godot.Input.GetJoyInfo(deviceIndex);
            var loan = new Loan
            {
                Id = deviceIndex, Name = Marshal.StringToCoTaskMemUTF8(global::Godot.Input.GetJoyName(deviceIndex)),
                Connected = true,
                // SDL3 handles its own mappings; Godot removes mapping_handled before exposing this info.
                LinearSdlAxes = info.ContainsKey("raw_name") && info.ContainsKey("vendor_id") &&
                                info.ContainsKey("product_id")
            };
            var handle = GCHandle.ToIntPtr(GCHandle.Alloc(loan));
            loans.Add(handle, loan);
            return handle;
        }

        public void Close(IntPtr controller)
        {
            Loan loan = Borrow(controller);
            if (loan.Connected) global::Godot.Input.StopJoyVibration(loan.Id);
            Marshal.FreeCoTaskMem(loan.Name);
            GCHandle.FromIntPtr(controller).Free();
            loans.Remove(controller);
        }

        public IntPtr GetJoystick(IntPtr controller)
        {
            Borrow(controller);
            return controller;
        }

        public int GetJoystickInstanceID(IntPtr joystick) => Borrow(joystick).Id;

        public int GetJoystickType(IntPtr joystick)
        {
            Borrow(joystick);
            return 1;
        }

        public IntPtr GetNamePointer(IntPtr controller) => Borrow(controller).Name;

        // Godot does not expose its SDL mapping string; never mislabel the GUID as a mapping.
        public IntPtr GetMappingPointer(IntPtr controller)
        {
            Borrow(controller);
            return IntPtr.Zero;
        }

        public void Free(IntPtr pointer) => Marshal.FreeCoTaskMem(pointer);

        public void ClearError()
        {
            /* Godot input operations do not use an SDL error buffer. */
        }

        public short GetAxis(IntPtr controller, SourceSDLGamePadAxis axis)
        {
            Loan loan = Borrow(controller);
            if (!loan.Connected || !window.HasFocus() || (int)axis < 0 || (int)axis > 5) return 0;
            return ToSourceAxis(global::Godot.Input.GetJoyAxis(loan.Id, (JoyAxis)(int)axis), axis, loan.LinearSdlAxes);
        }

        internal static short ToSourceAxis(float value, SourceSDLGamePadAxis axis, bool linearSdlAxes)
        {
            value = Math.Clamp(value, -1f, 1f);
            if (linearSdlAxes && axis is SourceSDLGamePadAxis.LEFTX or SourceSDLGamePadAxis.LEFTY or
                    SourceSDLGamePadAxis.RIGHTX or SourceSDLGamePadAxis.RIGHTY)
                return (short)Math.Round((value + 1f) * 32767.5f - 32768f);
            return (short)Math.Round(value * (value < 0 ? 32768f : 32767f));
        }

        private static JoyButton Button(SourceSDLGamePadButton button) => button switch
        {
            SourceSDLGamePadButton.A => JoyButton.A, SourceSDLGamePadButton.B => JoyButton.B,
            SourceSDLGamePadButton.X => JoyButton.X, SourceSDLGamePadButton.Y => JoyButton.Y,
            SourceSDLGamePadButton.BACK => JoyButton.Back, SourceSDLGamePadButton.GUIDE => JoyButton.Guide,
            SourceSDLGamePadButton.START => JoyButton.Start,
            SourceSDLGamePadButton.LEFTSTICK => JoyButton.LeftStick,
            SourceSDLGamePadButton.RIGHTSTICK => JoyButton.RightStick,
            SourceSDLGamePadButton.LEFTSHOULDER => JoyButton.LeftShoulder,
            SourceSDLGamePadButton.RIGHTSHOULDER => JoyButton.RightShoulder,
            SourceSDLGamePadButton.DPAD_UP => JoyButton.DpadUp, SourceSDLGamePadButton.DPAD_DOWN => JoyButton.DpadDown,
            SourceSDLGamePadButton.DPAD_LEFT => JoyButton.DpadLeft,
            SourceSDLGamePadButton.DPAD_RIGHT => JoyButton.DpadRight,
            SourceSDLGamePadButton.MISC1 => JoyButton.Misc1,
            SourceSDLGamePadButton.PADDLE1 => JoyButton.Paddle1,
            SourceSDLGamePadButton.PADDLE2 => JoyButton.Paddle2,
            SourceSDLGamePadButton.PADDLE3 => JoyButton.Paddle3,
            SourceSDLGamePadButton.PADDLE4 => JoyButton.Paddle4,
            SourceSDLGamePadButton.TOUCHPAD => JoyButton.Touchpad, _ => JoyButton.Invalid
        };

        public byte GetButton(IntPtr controller, SourceSDLGamePadButton button)
        {
            Loan loan = Borrow(controller);
            JoyButton mapped = Button(button);
            return loan.Connected && window.HasFocus() && mapped != JoyButton.Invalid &&
                   global::Godot.Input.IsJoyButtonPressed(loan.Id, mapped)
                ? (byte)1
                : (byte)0;
        }

        public int GetButtonBindingType(IntPtr controller, SourceSDLGamePadButton button)
        {
            Borrow(controller);
            return Button(button) == JoyButton.Invalid ? 0 : 1;
        }

        public int GetAxisBindingType(IntPtr controller, SourceSDLGamePadAxis axis)
        {
            Borrow(controller);
            return (int)axis is >= 0 and <= 5 ? 2 : 0;
        }

        public int Rumble(IntPtr controller, ushort left, ushort right, uint duration)
        {
            Loan loan = Borrow(controller);
            if (!loan.Connected) return -1;
            global::Godot.Input.StartJoyVibration(loan.Id, left / 65535f, right / 65535f,
                duration == uint.MaxValue ? 0 : duration / 1000f);
            return 0;
        }

        // These hardware capabilities are not exposed by Godot 4.5.1.
        public int RumbleTriggers(IntPtr controller, ushort left, ushort right, uint duration) => -1;
        public int HasLED(IntPtr controller) => 0;
        public int SetLED(IntPtr controller, byte red, byte green, byte blue) => -1;
        public int HasSensor(IntPtr controller, SourceSDLGamePadSensor sensor) => 0;
        public int IsSensorEnabled(IntPtr controller, SourceSDLGamePadSensor sensor) => 0;
        public int SetSensorEnabled(IntPtr controller, SourceSDLGamePadSensor sensor, int enabled) => -1;
        public int GetSensorData(IntPtr controller, SourceSDLGamePadSensor sensor, IntPtr data, int count) => -1;

        private ushort HardwareId(IntPtr handle, string key)
        {
            var info = global::Godot.Input.GetJoyInfo(Borrow(handle).Id);
            return info.ContainsKey(key) ? checked((ushort)info[key].AsInt32()) : (ushort)0;
        }

        public ushort GetJoystickVendor(IntPtr joystick) => HardwareId(joystick, "vendor_id");
        public ushort GetJoystickProduct(IntPtr joystick) => HardwareId(joystick, "product_id");

        public SourceSDLGamePadControllerType GetControllerType(IntPtr controller)
        {
            Borrow(controller);
            return SourceSDLGamePadControllerType.UNKNOWN;
        }

        public void Dispose()
        {
            global::Godot.Input.Singleton.JoyConnectionChanged -= JoyConnectionChanged;
            foreach (var handle in new List<IntPtr>(loans.Keys)) Close(handle);
            connected.Clear();
        }
    }
}
