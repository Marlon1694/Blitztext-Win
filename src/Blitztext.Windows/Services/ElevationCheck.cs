using System.Runtime.InteropServices;
using System.Security.Principal;
using static Blitztext.Windows.Interop.NativeMethods;

namespace Blitztext.Windows.Services;

/// <summary>
/// Windows does not let an app type into apps that run as administrator (UIPI), and it gives
/// no error. Knowing beforehand lets Blitztext say so and leave the text on the clipboard.
/// </summary>
public static class ElevationCheck
{
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, out int tokenInformation, int tokenInformationLength, out int returnLength);

    public static readonly bool IsCurrentProcessElevated = CheckCurrentProcess();

    private static bool CheckCurrentProcess()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True when the process runs elevated; false when unknown.</summary>
    public static bool IsElevated(uint processId)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero) return false;
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
            {
                // Access denied to the token of a process we can see: it runs at a higher level.
                return Marshal.GetLastWin32Error() == 5;
            }
            try
            {
                return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _) && elevated != 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
