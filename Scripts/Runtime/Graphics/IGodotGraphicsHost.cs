using System;
using Celeste.Core.Framework.Startup;
using Celeste.Graphics.Rendering.Devices;

// ReSharper disable once CheckNamespace
namespace Celeste.Godot.Runtime.Graphics
{
    internal interface IGodotGraphicsHost : IDisposable
    {
        ISourceGraphicsDeviceIOFactory DeviceFactory { get; }
        ISourceHostDrawTransportIO DrawTransport { get; }
        event Action? FramePresented;
        void AbortUnsubmittedDraws();
        void Pump();
        void StopPresenting();
        void WindowShown();
    }
}
