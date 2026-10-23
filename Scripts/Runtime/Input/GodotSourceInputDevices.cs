using System;
using Celeste.Core.Geometry;
using Celeste.Graphics.Rendering.Devices;
using Celeste.Input.Devices.Mouse;
using Celeste.Input.Devices.Text;
using Godot;
using Environment = System.Environment;

// ReSharper disable once CheckNamespace
namespace Celeste.Godot.Runtime.Input
{
    internal sealed class GodotSourceInputDevices : ISourceFnaInputClock, ISourceTextInputPlatform, ISourceClipboardIO,
        IDisposable
    {
        internal readonly Window Window;

        internal readonly ISourceHostWindowIO HostWindow;
        internal GodotSourceKeyboardDevice Keyboard { get; }
        internal GodotSourceMouseTouchDevice MouseTouch { get; }
        internal GodotSourceGamePadDevice GamePads { get; }
        private bool _textActive;

        internal GodotSourceInputDevices(Window window, ISourceHostWindowIO hostWindow)
        {
            Window = window;
            HostWindow = hostWindow;
            Keyboard = new(window);
            MouseTouch = new(window, hostWindow);
            GamePads = new(window);
        }

        public DateTime Now => DateTime.Now;
        public int TickCount => Environment.TickCount;

        public bool IsTextInputActive()
        {
            return _textActive;
        }

        public bool IsScreenKeyboardShown(IntPtr handle)
        {
            if (handle != HostWindow.Handle)
                throw new ArgumentException("The input window must be the shared host window.", nameof(handle));
            return DisplayServer.VirtualKeyboardGetHeight() > 0;
        }

        public void StartTextInput()
        {
            _textActive = true;
            DisplayServer.WindowSetImeActive(true, Window.GetWindowId());
        }

        public void StopTextInput()
        {
            DisplayServer.WindowSetImeActive(false, Window.GetWindowId());
            _textActive = false;
        }

        public void SetTextInputRectangle(IntRect rectangle)
        {
            DisplayServer.WindowSetImePosition(new(rectangle.X, rectangle.Y), Window.GetWindowId());
        }

        public string GetClipboardText()
        {
            return DisplayServer.ClipboardGet();
        }

        public void SetClipboardText(string value)
        {
            DisplayServer.ClipboardSet(value);
        }

        public void Dispose()
        {
            StopTextInput();
            GamePads.Dispose();
        }
    }
}
