using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Celeste.Godot.Runtime.Windowing
{
    internal sealed class GodotWindowAppearance : IDisposable
    {
        private const uint WmSettingChange = 0x001a;
        private const uint WmThemeChanged = 0x031a;
        private const uint WmNcDestroy = 0x0082;
        private static readonly UIntPtr SubclassId = new(1);
        private readonly SubclassCallback callback;
        private IntPtr window;

        internal GodotWindowAppearance(IntPtr window)
        {
            this.window = window;
            callback = WindowProcedure;
            if (!SetWindowSubclass(window, callback, SubclassId, UIntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            ApplyLightTitleBar(window);
        }

        public void Dispose()
        {
            if (window == IntPtr.Zero) return;
            RemoveWindowSubclass(window, callback, SubclassId);
            window = IntPtr.Zero;
        }

        private IntPtr WindowProcedure(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam,
            UIntPtr id, UIntPtr referenceData)
        {
            if (message == WmNcDestroy) Dispose();
            IntPtr result = DefSubclassProc(hwnd, message, wParam, lParam);
            // Godot applies the system's dark preference in its native handler.
            // Restore the game's light title bar after that handler, only on theme events.
            if (message is WmSettingChange or WmThemeChanged) ApplyLightTitleBar(hwnd);
            GC.KeepAlive(callback);
            return result;
        }

        private static void ApplyLightTitleBar(IntPtr window)
        {
            int enabled = 0;
            // Windows before 20H1 uses attribute 19; older systems have no dark title bar.
            if (DwmSetWindowAttribute(window, 20, ref enabled, sizeof(int)) < 0)
                DwmSetWindowAttribute(window, 19, ref enabled, sizeof(int));
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr SubclassCallback(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam,
            UIntPtr id, UIntPtr referenceData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowSubclass(IntPtr window, SubclassCallback callback, UIntPtr id,
            UIntPtr referenceData);

        [DllImport("comctl32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveWindowSubclass(IntPtr window, SubclassCallback callback, UIntPtr id);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }
}
