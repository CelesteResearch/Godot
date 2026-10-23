using System;
using System.Runtime.InteropServices;

// ReSharper disable once CheckNamespace
namespace Celeste.Godot.Runtime.Graphics
{
    internal static class GodotNativeGraphicsInterop
    {
        private const string Library = "CelesteGraphicsD3D11";

#pragma warning disable SYSLIB1054
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int SourceGpuExecutePhysicalPacket(IntPtr device, IntPtr packet);
#pragma warning restore SYSLIB1054

#pragma warning disable SYSLIB1054
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int SourceGpuConfigureFrameOutput(IntPtr device, uint width, uint height);
#pragma warning restore SYSLIB1054

#pragma warning disable SYSLIB1054
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int SourceGpuConfigureWindowOutput(IntPtr device, IntPtr window, uint width,
            uint height);
#pragma warning restore SYSLIB1054

#pragma warning disable SYSLIB1054
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int SourceGpuPresentWindowOutput(IntPtr device);
#pragma warning restore SYSLIB1054
    }
}
