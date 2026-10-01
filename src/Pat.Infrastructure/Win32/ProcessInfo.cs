using System.Text;

namespace Pat.Infrastructure.Win32;

/// <summary>Путь к exe по pid. Ограниченного доступа достаточно, работает и для процессов с повышенными правами.</summary>
internal static class ProcessInfo
{
    internal static string? GetImagePath(uint pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            return NativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref size)
                ? buffer.ToString(0, (int)size)
                : null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
