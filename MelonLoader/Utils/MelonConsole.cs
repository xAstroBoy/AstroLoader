using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MelonLoader.Utils;

internal static class MelonConsole
{
    private const int STD_OUTPUT_HANDLE = -11;
    
    internal static IntPtr ConsoleOutHandle = IntPtr.Zero;
    internal static FileStream ConsoleOutStream = null;
    internal static StreamWriter ConsoleOutWriter = null;
    
    internal static void Init()
    {
        if (MelonUtils.IsUnderWineOrSteamProton() || !MelonUtils.IsWindows || MelonLaunchOptions.Console.ShouldHide)
            return;
        
        ConsoleOutHandle = GetStdHandle(STD_OUTPUT_HANDLE);
        ConsoleOutStream =
        // This enables support for net2.0. Even though the old constructor is deprecated in net35, it's still functional
#if NET35
#pragma warning disable CS0618 // Type or member is obsolete
        new FileStream(ConsoleOutHandle, FileAccess.Write);
#pragma warning restore CS0618 // Type or member is obsolete
#else
            new FileStream(new SafeFileHandle(ConsoleOutHandle, false), FileAccess.Write);
#endif

        ConsoleOutWriter = new StreamWriter(ConsoleOutStream)
        {
            AutoFlush = true
        };
    }

    private static bool ShouldNotUseWriter()
        => (MelonUtils.IsUnderWineOrSteamProton()
            || !MelonUtils.IsWindows
            || MelonLaunchOptions.Console.ShouldHide
            || (ConsoleOutWriter == null));

    // ANSI/Pastel color escapes ([38;2;r;g;bm etc). On Android these can't be rendered by logcat and show up
    // as garbled junk around the timestamp/text, so strip them on non-Windows (keep them for the PC console).
    private static readonly System.Text.RegularExpressions.Regex AnsiRegex =
        new System.Text.RegularExpressions.Regex(@"\x1B\[[0-9;]*m", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string CleanForPlatform(string txt)
    {
        if (txt != null && !MelonUtils.IsWindows)
            return AnsiRegex.Replace(txt, "");
        return txt;
    }

    internal static void WriteLine(string txt)
    {
        BootstrapInterop.NativeLogConsole(CleanForPlatform(txt));
    }

    internal static void WriteLine(object txt)
    {
        BootstrapInterop.NativeLogConsole(CleanForPlatform(txt?.ToString()));
    }

    internal static void WriteLine()
    {
        BootstrapInterop.NativeLogConsole("");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

}