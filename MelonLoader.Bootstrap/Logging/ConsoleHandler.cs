using MelonLoader.Logging;
using System.Runtime.InteropServices;
using System.Text;

namespace MelonLoader.Bootstrap;

internal static partial class ConsoleHandler
{
#if WINDOWS
    internal static nint OutputHandle;
    internal static nint ErrorHandle;

    private static nint outputHandle;
#endif

    public static bool IsOpen { get; private set; }
    public static bool HasOwnWindow { get; private set; }

    public static void OpenConsole(bool onTop, string? title)
    {
#if WINDOWS
        // Do not create a new window if a window already exists or the output is being redirected
        var consoleWindow = WindowsNative.GetConsoleWindow();
        var stdOut = WindowsNative.GetStdHandle(WindowsNative.StdOutputHandle);
        if (consoleWindow == 0 && stdOut == 0)
        {
            WindowsNative.AllocConsole();
            consoleWindow = WindowsNative.GetConsoleWindow();
            if (consoleWindow == 0)
                return;

            HasOwnWindow = true;

            if (onTop)
                WindowsNative.SetWindowPos(consoleWindow, -1, 0, 0, 0, 0, 0x0001 | 0x0002);
        }

        if (consoleWindow != 0)
#endif
        {
            if (title != null)
                Console.Title = title;
        }

#if WINDOWS
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        Console.SetIn(new StreamReader(Console.OpenStandardInput()));

        Console.OutputEncoding = Encoding.UTF8;

        OutputHandle = WindowsNative.GetStdHandle(WindowsNative.StdOutputHandle);
        ErrorHandle = WindowsNative.GetStdHandle(WindowsNative.StdErrorHandle);
#endif

        IsOpen = true;
    }

    public static void NullHandles()
    {
#if WINDOWS
        WindowsNative.SetStdHandle(WindowsNative.StdOutputHandle, 0);
        WindowsNative.SetStdHandle(WindowsNative.StdErrorHandle, 0);
#endif
    }

    public static void ResetHandles()
    {
#if WINDOWS
        WindowsNative.SetStdHandle(WindowsNative.StdOutputHandle, OutputHandle);
        WindowsNative.SetStdHandle(WindowsNative.StdErrorHandle, ErrorHandle);
#endif
    }

    public static ConsoleColor GetClosestConsoleColor(ColorARGB color)
    {
        var index = color.R > 128 | color.G > 128 | color.B > 128 ? 8 : 0; // Bright bit
        index |= color.R > 64 ? 4 : 0; // Red bit
        index |= color.G > 64 ? 2 : 0; // Green bit
        index |= color.B > 64 ? 1 : 0; // Blue bit
        return (ConsoleColor)index;
    }
}