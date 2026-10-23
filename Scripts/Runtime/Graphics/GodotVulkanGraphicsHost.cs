using System;
using Celeste.Core.Framework.Startup;
using Celeste.Graphics.Rendering.Devices;
using Celeste.Graphics.Vulkan.Devices;
using Celeste.Graphics.Vulkan.Submission;
using Godot;

// ReSharper disable once CheckNamespace
namespace Celeste.Godot.Runtime.Graphics
{
    internal sealed class GodotVulkanGraphicsHost : IGodotGraphicsHost, IVulkanGraphicsSubmission
    {
        private readonly VulkanWindowGraphicsSubmission _submission;
        private readonly VulkanGraphicsDeviceIOFactory _graphics;
        private bool _presenting = true;

        internal GodotVulkanGraphicsHost(ISourceHostWindowIO window, Window godotWindow)
        {
            var surfaceKind = DisplayServer.GetName().ToLowerInvariant() switch
            {
                "windows" => VulkanWindowSurfaceKind.Win32,
                "x11" => VulkanWindowSurfaceKind.Xlib,
                "wayland" => VulkanWindowSurfaceKind.Wayland,
                "macos" => VulkanWindowSurfaceKind.MetalView,
                _ => throw new NotSupportedException(
                    "Shared Vulkan requires a native Windows, X11, Wayland or macOS window."),
            };
            var windowId = godotWindow.GetWindowId();
            var nativeWindow = new IntPtr(DisplayServer.WindowGetNativeHandle(
                surfaceKind == VulkanWindowSurfaceKind.MetalView
                    ? DisplayServer.HandleType.WindowView
                    : DisplayServer.HandleType.WindowHandle,
                windowId));
            var nativeDisplay = surfaceKind is VulkanWindowSurfaceKind.Xlib or VulkanWindowSurfaceKind.Wayland
                ? new(DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.DisplayHandle, windowId))
                : IntPtr.Zero;
            _submission = new(window, surfaceKind, nativeWindow, nativeDisplay);
            // The factory must call this wrapper, so first-frame visibility observes Present.
            _graphics = new(this, window.Handle);
        }

        public ISourceGraphicsDeviceIOFactory DeviceFactory => _graphics;
        public ISourceHostDrawTransportIO DrawTransport => _graphics;
        public event Action? FramePresented;

        public IntPtr AcquireDevice()
        {
            return _submission.AcquireDevice();
        }

        public void ReleaseDevice(IntPtr device)
        {
            _submission.ReleaseDevice(device);
        }

        public void PrepareDraw()
        {
            _submission.PrepareDraw();
        }

        public void SetPresentationInterval(SourcePresentInterval interval)
        {
            _submission.SetPresentationInterval(interval);
        }

        public IntPtr AcquireOutput(IntPtr device)
        {
            return _submission.AcquireOutput(device);
        }

        public void Submit(IntPtr packet, bool waitForCompletion)
        {
            _submission.Submit(packet, waitForCompletion);
        }

        public void AbortUnsubmittedDraws()
        {
            _graphics.AbortUnsubmittedDraws();
        }

        public void Pump()
        {
            _submission.Pump();
        }

        public void PresentCompleted()
        {
            // Finish any acquired image even during shutdown; do not strand a swapchain loan.
            _submission.PresentCompleted();
            if (_presenting) FramePresented?.Invoke();
        }

        public void WindowShown()
        {
            // The image has already been consumed by Present. The next source frame acquires
            // a fresh image; repeating Present here would violate the shared window contract.
        }

        public void StopPresenting()
        {
            _presenting = false;
        }

        public void Dispose()
        {
            _submission.Dispose();
        }
    }
}
