using System;
using Celeste.Core.Framework.Startup;
using Celeste.Graphics.Direct3D12.Devices;
using Celeste.Graphics.Direct3D12.Submission;
using Celeste.Graphics.Rendering.Devices;

namespace Celeste.Godot.Runtime.Graphics
{
    internal sealed class GodotDirect3D12GraphicsHost : IGodotGraphicsHost
    {
        private readonly Win32Direct3D12GraphicsSubmission submission;

        internal GodotDirect3D12GraphicsHost(ISourceHostWindowIO window, IntPtr nativeWindow)
        {
            if (nativeWindow == IntPtr.Zero)
                throw new NotSupportedException(
                    "Godot Direct3D12 requires a real Windows game window; headless output is not supported.");
            submission = new Win32Direct3D12GraphicsSubmission(window, nativeWindow);
        }

        public ISourceGraphicsDeviceIOFactory DeviceFactory => submission.DeviceFactory;
        public ISourceHostDrawTransportIO DrawTransport => submission.DrawTransport;

        // Reveal the initial window when its frame is ready, before DXGI Present.
        // Waiting for a visible-present notification would deadlock against occlusion.
        public event Action? FramePresented
        {
            add => submission.FirstFrameReady += value;
            remove => submission.FirstFrameReady -= value;
        }

        public void Pump() => submission.Pump();
        public void StopPresenting() => submission.StopPresenting();

        public void AbortUnsubmittedDraws() =>
            ((Direct3D12GraphicsDeviceIOFactory)submission.DeviceFactory).AbortUnsubmittedDraws();

        public void WindowShown()
        {
            // The first Present continues after the visibility callback; do not reenter it.
        }

        public void Dispose() => submission.Dispose();
    }
}
