using System;
using System.Runtime.InteropServices;

namespace AtProtoUnity
{
    static class AtProtoWindowFocus
    {
        public static IntPtr Capture()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            return GetForegroundWindow();
#else
            return IntPtr.Zero;
#endif
        }

        public static void Restore(IntPtr window)
        {
            if (window == IntPtr.Zero)
                return;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (IsIconic(window))
                ShowWindow(window, ShowWindowRestore);
            SetForegroundWindow(window);
#endif
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        const int ShowWindowRestore = 9;

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr window, int command);
#endif
    }
}