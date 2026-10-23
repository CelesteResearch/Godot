using System;
using System.Collections.Concurrent;
using System.Threading;
using Celeste.Core.Framework.Startup;
using Celeste.Platform.Native.Sdl;
using Celeste.Graphics.Direct3D11.Submission;
using Celeste.Graphics.Direct3D11.Devices;
using Celeste.Graphics.Rendering.Devices;

namespace Celeste.Godot.Runtime.Graphics
{
    public sealed class GodotNativeGraphicsSubmission : IDirect3D11GraphicsSubmission, IGodotGraphicsHost
    {
        private readonly ISourceHostWindowIO window;
        private readonly int ownerThread;
        private readonly ConcurrentQueue<GodotGraphicsSubmissionRequest> requests = new();
        private readonly object requestGate = new();
        private IntPtr device;
        private Direct3D11GraphicsContext? context;
        private ulong deviceGeneration;
        private int width;
        private int height;
        private bool presenting = true;
        private bool acceptingWorkerRequests = true;
        private bool disposed;

        private readonly Direct3D11GraphicsDeviceIOFactory graphics;
        public ISourceGraphicsDeviceIOFactory DeviceFactory => graphics;
        public ISourceHostDrawTransportIO DrawTransport => graphics;
        public void WindowShown() => PresentCompleted();
        public void AbortUnsubmittedDraws() => Direct3D11GraphicsDeviceIO.AbortUnsubmittedPhysicalPrefix();
        public event Action? FramePresented;

        public GodotNativeGraphicsSubmission(ISourceHostWindowIO window, int ownerThread)
        {
            this.window = window ?? throw new ArgumentNullException(nameof(window));
            if (ownerThread <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(ownerThread));
            }

            this.ownerThread = ownerThread;
            graphics = new Direct3D11GraphicsDeviceIOFactory(this, window.Handle, new NativeSourceSDLHintDevice());
        }

        public IntPtr AcquireDevice()
        {
            if (Thread.CurrentThread.ManagedThreadId == ownerThread)
            {
                return AcquireOnOwner();
            }

            var acquired = IntPtr.Zero;
            InvokeOnOwner(() => acquired = AcquireOnOwner());
            return acquired;
        }

        private IntPtr AcquireOnOwner()
        {
            RequireOwner();
            // A game can restart while this host keeps its independently owned context.
            context ??= new Direct3D11GraphicsContext();
            IntPtr acquired = context.AcquireDevice();
            RequireSuccess(acquired != IntPtr.Zero ? 1 : 0);
            ulong generation = context.Generation;
            if (device != acquired || deviceGeneration != generation)
            {
                device = acquired;
                deviceGeneration = generation;
                // The previous generation's host output was destroyed with the device.
                // Equal window dimensions must still allocate output for the new context.
                width = 0;
                height = 0;
                PrepareDraw();
            }

            return device;
        }

        public void SubmitAndWait(IntPtr sealedPhysicalPacket)
        {
            if (Thread.CurrentThread.ManagedThreadId == ownerThread)
            {
                SubmitOnOwner(sealedPhysicalPacket);
                return;
            }

            // Only workers enqueue. Normal owner-thread draws stay on the direct path.
            InvokeOnOwner(() => SubmitOnOwner(sealedPhysicalPacket));
        }

        private void SubmitOnOwner(IntPtr packet)
        {
            RequireOwner();
            if (packet == IntPtr.Zero || device == IntPtr.Zero)
            {
                throw new InvalidOperationException("A real source device and sealed packet are required.");
            }

            // Execute borrows the caller's owner loan, which DeviceIO releases afterwards.
            RequireSuccess(GodotNativeGraphicsInterop.SourceGpuExecutePhysicalPacket(device, packet));
            RequireSuccess(Direct3D11GraphicsPackets.GetCompletion(packet) == 1 ? 1 : 0);
        }

        public void PrepareDraw()
        {
            RequireOwner();
            window.GetDrawableSize(out var actualWidth, out var actualHeight);
            if (actualWidth <= 0 || actualHeight <= 0)
            {
                // Minimized windows retain the last complete native output allocation.
                if (width > 0 && height > 0)
                {
                    return;
                }

                throw new InvalidOperationException("The source host has no positive initial drawable size.");
            }

            if (width == actualWidth && height == actualHeight)
            {
                return;
            }

            RequireSuccess(window.Handle == IntPtr.Zero
                ? GodotNativeGraphicsInterop.SourceGpuConfigureFrameOutput(device,
                    checked((uint)actualWidth), checked((uint)actualHeight))
                : GodotNativeGraphicsInterop.SourceGpuConfigureWindowOutput(device, window.Handle,
                    checked((uint)actualWidth), checked((uint)actualHeight)));
            width = actualWidth;
            height = actualHeight;
        }

        public void PresentCompleted()
        {
            RequireOwner();
            if (!presenting || window.Handle == IntPtr.Zero)
            {
                return;
            }

            // The shared device has completed its original crop/scale Present packet.
            RequireSuccess(GodotNativeGraphicsInterop.SourceGpuPresentWindowOutput(device));
            FramePresented?.Invoke();
        }

        public void SetPresentationSyncInterval(int count)
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
            {
                InvokeOnOwner(() => SetPresentationSyncInterval(count));
                return;
            }

            RequireOwner();
            // SourceGpuSetPresentationInterval already stores the original interval
            // on the actual native device. The borrowed HWND swapchain consumes it.
        }

        private void InvokeOnOwner(Action operation)
        {
            var request = new GodotGraphicsSubmissionRequest(operation);
            lock (requestGate)
            {
                if (disposed || !acceptingWorkerRequests)
                {
                    throw new ObjectDisposedException(nameof(GodotNativeGraphicsSubmission));
                }

                requests.Enqueue(request);
            }

            request.Wait();
        }

        // The existing Godot callback pumps around ContinueSourceHostRun. No loader Join
        // or synchronous owner-thread wait may replace that startup/update polling loop.
        public void Pump()
        {
            RequireOwner();
            while (requests.TryDequeue(out var request))
            {
                request.Execute();
            }
        }

        public void StopPresenting()
        {
            RequireOwner();
            presenting = false;
            lock (requestGate)
            {
                acceptingWorkerRequests = false;
                CancelPendingRequests();
            }
        }

        public void Dispose()
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
            {
                throw new InvalidOperationException("Dispose the native graphics submission on its owner thread.");
            }

            lock (requestGate)
            {
                if (disposed)
                {
                    return;
                }

                presenting = false;
                acceptingWorkerRequests = false;
                disposed = true;
                CancelPendingRequests();
            }

            context?.Dispose();
            context = null;
            device = IntPtr.Zero;
        }

        private void CancelPendingRequests()
        {
            while (requests.TryDequeue(out var request))
            {
                request.Cancel(new ObjectDisposedException(nameof(GodotNativeGraphicsSubmission)));
            }
        }

        private void RequireOwner()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(GodotNativeGraphicsSubmission));
            }

            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
            {
                throw new InvalidOperationException(
                    "Native GPU execution and Godot output require the actual owner thread.");
            }
        }

        private static void RequireSuccess(int success)
        {
            if (success == 0)
            {
                throw new InvalidOperationException(Direct3D11GraphicsPackets.LastError ??
                                                    "The native source graphics operation failed.");
            }
        }
    }
}
