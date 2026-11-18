using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Svidetel;

internal static partial class InteractiveProcess
{
    private const uint TokenQuery = 0x0008;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    public static bool CanRequestClose(Finding item)
    {
        try { using var process = OpenVerified(item); return process != null; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return false; }
    }

    public static void RequestClose(Finding item)
    {
        using var process = OpenVerified(item);
        if (process == null || !process.CloseMainWindow())
            throw new InvalidOperationException("Приложение недоступно, изменилось или не принимает запрос на закрытие.");
    }

    private static Process? OpenVerified(Finding item)
    {
        if (item.Kind != "process" || item.ProcessId <= 0 || item.ProcessId == Environment.ProcessId ||
            item.ProcessStartedAt is null || string.IsNullOrWhiteSpace(item.Executable))
            return null;

        var process = Process.GetProcessById(item.ProcessId);
        var accepted = false;
        try
        {
            using var self = Process.GetCurrentProcess();
            using var current = WindowsIdentity.GetCurrent();
            if (process.SessionId == 0 || process.SessionId != self.SessionId || process.MainWindowHandle == 0 ||
                Math.Abs((process.StartTime.ToUniversalTime() - item.ProcessStartedAt.Value.UtcDateTime).TotalMilliseconds) > 10 ||
                !string.Equals(process.MainModule?.FileName, item.Executable, StringComparison.OrdinalIgnoreCase) ||
                item.Executable.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\", StringComparison.OrdinalIgnoreCase) ||
                !OpenProcessToken(process.Handle, TokenQuery, out var handle))
                return null;

            using (handle)
            using (var owner = new WindowsIdentity(handle.DangerousGetHandle()))
                accepted = current.User is { } currentSid && owner.User?.Equals(currentSid) == true;

            return accepted ? process : null;
        }
        finally
        {
            if (!accepted) process.Dispose();
        }
    }
}
