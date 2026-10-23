using System;
using System.Collections.Concurrent;
using Celeste.Core.Framework.Startup;
using Celeste.Graphics.Direct3D11.Devices;
using Celeste.Graphics.Direct3D11.Submission;
using Celeste.Graphics.Rendering.Devices;
using Celeste.Platform.Native.Sdl;

// ReSharper disable once CheckNamespace
namespace Celeste.Godot.Runtime.Graphics
{
    public sealed class GodotNativeGraphicsSubmission : IDirect3D11GraphicsSubmission, IGodotGraphicsHost
    {
        private readonly ISourceHostWindowIO _window;
        private readonly int _ownerThread;
        private readonly ConcurrentQueue<GodotGraphicsSubmissionRequest> _requests = new();
        private readonly object _requestGate = new();
        private IntPtr _device;
        private Direct3D11GraphicsContext? _context;
        private ulong _deviceGeneration;
        private int _width;
        private int _height;
        private bool _presenting = true;
        private bool _acceptingWorkerRequests = true;
        private bool _disposed;

        private readonly Direct3D11GraphicsDeviceIOFactory _graphics;
        public ISourceGraphicsDeviceIOFactory DeviceFactory => _graphics;
        public ISourceHostDrawTransportIO DrawTransport => _graphics;

        public void WindowShown()
        {
            PresentCompleted();
        }

        public void AbortUnsubmittedDraws()
        {
            Direct3D11GraphicsDeviceIO.AbortUnsubmittedPhysicalPrefix();
        }

        public event Action? FramePresented;

        public GodotNativeGraphicsSubmission(ISourceHostWindowIO window, int ownerThread)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
#pragma warning disable CA1512
            if (ownerThread <= 0) throw new ArgumentOutOfRangeException(nameof(ownerThread));
#pragma warning restore CA1512

            _ownerThread = ownerThread;
            _graphics = new(this, window.Handle, new NativeSourceSDLHintDevice());
        }

        public IntPtr AcquireDevice()
        {
            if (Environment.CurrentManagedThreadId == _ownerThread) return AcquireOnOwner();

            var acquired = IntPtr.Zero;
            InvokeOnOwner(() => acquired = AcquireOnOwner());
            return acquired;
        }

        private IntPtr AcquireOnOwner()
        {
            RequireOwner();
            // A game can restart while this host keeps its independently owned context.
            _context ??= new();
            var acquired = _context.AcquireDevice();
            RequireSuccess(acquired != IntPtr.Zero ? 1 : 0);
            var generation = _context.Generation;
            if (_device == acquired && _deviceGeneration == generation) return _device;
            _device = acquired;
            _deviceGeneration = generation;
            // The previous generation's host output was destroyed with the device.
            // Equal window dimensions must still allocate output for the new context.
            _width = 0;
            _height = 0;
            PrepareDraw();

            return _device;
        }

        public void SubmitAndWait(IntPtr sealedPhysicalPacket)
        {
            if (Environment.CurrentManagedThreadId == _ownerThread)
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
            if (packet == IntPtr.Zero || _device == IntPtr.Zero)
                throw new InvalidOperationException("A real source device and sealed packet are required.");

            // Execute borrows the caller's owner loan, which DeviceIO releases afterwards.
            RequireSuccess(GodotNativeGraphicsInterop.SourceGpuExecutePhysicalPacket(_device, packet));
            RequireSuccess(Direct3D11GraphicsPackets.GetCompletion(packet) == 1 ? 1 : 0);
        }

        public void PrepareDraw()
        {
            RequireOwner();
            _window.GetDrawableSize(out var actualWidth, out var actualHeight);
            if (actualWidth <= 0 || actualHeight <= 0)
            {
                // Minimized windows retain the last complete native output allocation.
                if (_width > 0 && _height > 0) return;

                throw new InvalidOperationException("The source host has no positive initial drawable size.");
            }

            if (_width == actualWidth && _height == actualHeight) return;

            RequireSuccess(_window.Handle == IntPtr.Zero
                ? GodotNativeGraphicsInterop.SourceGpuConfigureFrameOutput(_device,
                    checked((uint)actualWidth), checked((uint)actualHeight))
                : GodotNativeGraphicsInterop.SourceGpuConfigureWindowOutput(_device, _window.Handle,
                    checked((uint)actualWidth), checked((uint)actualHeight)));
            _width = actualWidth;
            _height = actualHeight;
        }

        public void PresentCompleted()
        {
            RequireOwner();
            if (!_presenting || _window.Handle == IntPtr.Zero) return;

            // The shared device has completed its original crop/scale Present packet.
            RequireSuccess(GodotNativeGraphicsInterop.SourceGpuPresentWindowOutput(_device));
            FramePresented?.Invoke();
        }

        public void SetPresentationSyncInterval(int count)
        {
            if (Environment.CurrentManagedThreadId != _ownerThread)
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
            lock (_requestGate)
            {
#pragma warning disable CA1513
                // ReSharper disable once ConvertIfStatementToReturnStatement
                if (_disposed || !_acceptingWorkerRequests)
                    throw new ObjectDisposedException(nameof(GodotNativeGraphicsSubmission));
#pragma warning restore CA1513

                _requests.Enqueue(request);
            }

            request.Wait();
        }

        // The existing Godot callback pumps around ContinueSourceHostRun. No loader Join
        // or synchronous owner-thread wait may replace that startup/update polling loop.
        public void Pump()
        {
            RequireOwner();
            while (_requests.TryDequeue(out var request)) request.Execute();
        }

        public void StopPresenting()
        {
            RequireOwner();
            _presenting = false;
            lock (_requestGate)
            {
                _acceptingWorkerRequests = false;
                CancelPendingRequests();
            }
        }

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _ownerThread)
                throw new InvalidOperationException("Dispose the native graphics submission on its owner thread.");

            lock (_requestGate)
            {
                if (_disposed) return;

                _presenting = false;
                _acceptingWorkerRequests = false;
                _disposed = true;
                CancelPendingRequests();
            }

            _context?.Dispose();
            _context = null;
            _device = IntPtr.Zero;
        }

        private void CancelPendingRequests()
        {
            while (_requests.TryDequeue(out var request))
                request.Cancel(new ObjectDisposedException(nameof(GodotNativeGraphicsSubmission)));
        }

        private void RequireOwner()
        {
#pragma warning disable CA1513
            // ReSharper disable once ConvertIfStatementToReturnStatement
            if (_disposed) throw new ObjectDisposedException(nameof(GodotNativeGraphicsSubmission));
#pragma warning restore CA1513

            if (Environment.CurrentManagedThreadId != _ownerThread)
                throw new InvalidOperationException(
                    "Native GPU execution and Godot output require the actual owner thread.");
        }

        private static void RequireSuccess(int success)
        {
            if (success == 0)
                throw new InvalidOperationException(Direct3D11GraphicsPackets.LastError ??
                                                    "The native source graphics operation failed.");
        }
    }
}
