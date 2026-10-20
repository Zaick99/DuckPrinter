using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DuckPrinterManager.Ui;

/// <summary>
/// Sfocatura di Windows dietro la finestra (effetto vetro). Funziona con la finestra senza bordi e
/// AllowsTransparency: SetWindowCompositionAttribute con ACCENT_ENABLE_ACRYLICBLURBEHIND su
/// Windows 11, BLURBEHIND su Windows 10 (l'acrilico li' rallenta il trascinamento). La tinta la
/// danno i token semitrasparenti del tema (BgWindow, BgSidebarGlass...), non l'accento.
/// </summary>
public static class Backdrop
{
    private enum AccentState { Disabled = 0, BlurBehind = 3, AcrylicBlurBehind = 4 }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState State;
        public int Flags;
        public uint GradientColor;   // AABBGGRR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionAttributeData
    {
        public int Attribute;        // 19 = WCA_ACCENT_POLICY
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Attiva o toglie la sfocatura. Restituisce false se il sistema non la consente.</summary>
    public static bool Apply(Window window, bool on)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            var policy = new AccentPolicy
            {
                State = !on ? AccentState.Disabled : IsWindows11 ? AccentState.AcrylicBlurBehind : AccentState.BlurBehind,
                Flags = 0,
                GradientColor = 0x01000000,   // quasi nessuna tinta di sistema
            };
            var size = Marshal.SizeOf<AccentPolicy>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new CompositionAttributeData { Attribute = 19, Data = ptr, SizeOfData = size };
                if (SetWindowCompositionAttribute(hwnd, ref data) == 0) return false;
            }
            finally { Marshal.FreeHGlobal(ptr); }

            // Windows 11: angoli arrotondati da DWM, cosi' la sfocatura non sborda dagli angoli
            if (IsWindows11)
            {
                var pref = on ? 2 : 0;   // DWMWCP_ROUND / DEFAULT
                DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
            }
            return true;
        }
        catch { return false; }
    }
}
