using System.Runtime.InteropServices;

namespace OpenBancho;

/// <summary>Turns off the console's QuickEdit mode on Windows, so clicking the server window doesn't freeze it.</summary>
static class ConsoleMode
{
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr handle, uint mode);

    public static void DisableQuickEdit()
    {
        if (!OperatingSystem.IsWindows()) return;
        const uint QuickEdit = 0x0040, ExtendedFlags = 0x0080;
        var input = GetStdHandle(-10); // STD_INPUT_HANDLE
        if (GetConsoleMode(input, out var mode)) SetConsoleMode(input, (mode & ~QuickEdit) | ExtendedFlags);
    }
}
