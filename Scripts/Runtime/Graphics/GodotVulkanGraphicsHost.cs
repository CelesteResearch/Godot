using System;
using Celeste.Core.Framework.Startup;
using Celeste.Graphics.Rendering.Devices;
using Celeste.Graphics.Vulkan.Devices;
using Celeste.Graphics.Vulkan.Submission;
using Godot;

namespace Celeste.Godot.Runtime.Graphics
{
    internal sealed class GodotVulkanGraphicsHost : IGodotGraphicsHost, IVulkanGraphicsSubmission
    {
        private readonly VulkanWindowGraphicsSubmission submission;
        private readonly VulkanGraphicsDeviceIOFactory graphics;
        private bool presenting = true;

        internal GodotVulkanGraphicsHost(ISourceHostWindowIO window, Window godotWindow)
        {
            VulkanWindowSurfaceKind surfaceKind = DisplayServer.GetName().ToLowerInvariant() switch
            {
                "windows" => VulkanWindowSurfaceKind.Win32,
                "x11" => VulkanWindowSurfaceKind.Xlib,
                "wayland" => VulkanWindowSurfaceKind.Wayland,
                "macos" => VulkanWindowSurfaceKind.MetalView,
                _ => throw new NotSupportedException(
                    "Shared Vulkan requires a native Windows, X11, Wayland or macOS window.")
            };
            int windowId = godotWindow.GetWindowId();
            var nativeWindow = new IntPtr(DisplayServer.WindowGetNativeHandle(
                surfaceKind == VulkanWindowSurfaceKind.MetalView
                    ? DisplayServer.HandleType.WindowView
                    : DisplayServer.HandleType.WindowHandle,
                windowId));
            var nativeDisplay = surfaceKind is VulkanWindowSurfaceKind.Xlib or VulkanWindowSurfaceKind.Wayland
                ? new IntPtr(DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.DisplayHandle, windowId))
                : IntPtr.Zero;
            submission = new VulkanWindowGraphicsSubmission(window, surfaceKind, nativeWindow, nativeDisplay);
            // The factory must call this wrapper, so first-frame visibility observes Present.
            graphics = new VulkanGraphicsDeviceIOFactory(this, window.Handle);
        }

        public ISourceGraphicsDeviceIOFactory DeviceFactory => graphics;
        public ISourceHostDrawTransportIO DrawTransport => graphics;
        public event Action? FramePresented;
        public IntPtr AcquireDevice() => submission.AcquireDevice();
        public void ReleaseDevice(IntPtr device) => submission.ReleaseDevice(device);
        public void PrepareDraw() => submission.PrepareDraw();

        public void SetPresentationInterval(SourcePresentInterval interval) =>
            submission.SetPresentationInterval(interval);

        public IntPtr AcquireOutput(IntPtr device) => submission.AcquireOutput(device);
        public void Submit(IntPtr packet, bool waitForCompletion) => submission.Submit(packet, waitForCompletion);
        public void AbortUnsubmittedDraws() => graphics.AbortUnsubmittedDraws();
        public void Pump() => submission.Pump();

        public void PresentCompleted()
        {
            // Finish any acquired image even during shutdown; do not strand a swapchain loan.
            submission.PresentCompleted();
            if (presenting) FramePresented?.Invoke();
        }

        public void WindowShown()
        {
            // The image has already been consumed by Present. The next source frame acquires
            // a fresh image; repeating Present here would violate the shared window contract.
        }

        public void StopPresenting() => presenting = false;
        public void Dispose() => submission.Dispose();
    }
}
