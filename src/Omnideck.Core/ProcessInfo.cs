using System.Diagnostics;
using Omnideck.Core.Interop;

namespace Omnideck.Core;

/// <summary>Cheap process queries that work for elevated processes too (limited-information access).</summary>
public static class ProcessInfo
{
    public static string? GetExecutablePath(int processId)
    {
        if (processId <= 0) return null;
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            int size = buffer.Length;
            return NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? new string(buffer, 0, size)
                : null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    public static bool IsAlive(int processId)
    {
        if (processId <= 0) return true;
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            // Access denied still means the process exists; anything else means it's gone.
            return System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 5;
        }
        try
        {
            return NativeMethods.GetExitCodeProcess(handle, out int code) && code == NativeMethods.StillActive;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>Human-friendly app name: exe FileDescription, falling back to the file name.</summary>
    public static string GetFriendlyName(string? exePath, int processId)
    {
        if (!string.IsNullOrEmpty(exePath))
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(exePath).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
            }
            catch
            {
                // Unreadable version resource — fall through.
            }
            return Path.GetFileNameWithoutExtension(exePath);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return $"PID {processId}";
        }
    }
}
