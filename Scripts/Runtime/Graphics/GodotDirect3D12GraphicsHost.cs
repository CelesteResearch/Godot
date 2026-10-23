using System;
using Celeste.Core.Framework.Startup;
using Celeste.Graphics.Direct3D12.Devices;
using Celeste.Graphics.Direct3D12.Submission;
using Celeste.Graphics.Rendering.Devices;

// ReSharper disable once CheckNamespace
namespace Celeste.Godot.Runtime.Graphics
{
    internal sealed class GodotDirect3D12GraphicsHost : IGodotGraphicsHost
    {
        private readonly Win32Direct3D12GraphicsSubmission _submission;

        internal GodotDirect3D12GraphicsHost(ISourceHostWindowIO window, IntPtr nativeWindow)
        {
            if (nativeWindow == IntPtr.Zero)
                throw new NotSupportedException(
                    "Godot Direct3D12 requires a real Windows game window; headless output is not supported.");
            _submission = new(window, nativeWindow);
        }

        public ISourceGraphicsDeviceIOFactory DeviceFactory => _submission.DeviceFactory;
        public ISourceHostDrawTransportIO DrawTransport => _submission.DrawTransport;

        // Reveal the initial window when its frame is ready, before DXGI Present.
        // Waiting for a visible-present notification would deadlock against occlusion.
        public event Action? FramePresented
        {
            add => _submission.FirstFrameReady += value;
            remove => _submission.FirstFrameReady -= value;
        }

        public void Pump()
        {
            _submission.Pump();
        }

        public void StopPresenting()
        {
            _submission.StopPresenting();
        }

        public void AbortUnsubmittedDraws()
        {
            ((Direct3D12GraphicsDeviceIOFactory)_submission.DeviceFactory).AbortUnsubmittedDraws();
        }

        public void WindowShown()
        {
            // The first Present continues after the visibility callback; do not reenter it.
        }

        public void Dispose()
        {
            _submission.Dispose();
        }
    }
}
