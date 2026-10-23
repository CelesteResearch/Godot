using System;
using System.Runtime.InteropServices;

namespace Celeste.Godot.Runtime.Graphics
{
    internal static class GodotNativeGraphicsInterop
    {
        private const string Library = "CelesteGraphicsD3D11";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int SourceGpuExecutePhysicalPacket(IntPtr device, IntPtr packet);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int SourceGpuConfigureFrameOutput(IntPtr device, uint width, uint height);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int SourceGpuConfigureWindowOutput(IntPtr device, IntPtr window, uint width,
            uint height);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int SourceGpuPresentWindowOutput(IntPtr device);
    }
}
