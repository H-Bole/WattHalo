using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using WattMonitor.Core;

namespace WattMonitor.App;

internal static class ProcessActions
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    { "System", "Registry", "Secure System", "Idle", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "explorer", "WattMonitor.App", "WattMonitor.Service" };

    public static string Close(ProcessSample target, bool force)
    {
        try
        {
            if (!target.CanClose || target.Pid <= 4 || target.Pid == Environment.ProcessId || ProtectedNames.Contains(target.Name))
                return "该进程不允许从监测工具关闭。";
            using var process = Process.GetProcessById(target.Pid);
            if (process.StartTime.ToUniversalTime().Ticks != target.StartTimeUtcTicks ||
                process.SessionId != Process.GetCurrentProcess().SessionId || ProtectedNames.Contains(process.ProcessName))
                return "目标已变化或不属于当前会话，请刷新后重试。";
            if (target.ExecutablePath is null || !string.Equals(process.MainModule?.FileName, target.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                return "无法核对目标软件路径，操作已取消。";
            if (IsProcessCritical(process.Handle, out var critical) && critical)
                return "Windows 关键进程不可关闭。";
            if (!OpenProcessToken(process.Handle, 8, out var token)) return "无法验证进程所有者，操作已取消。";
            try
            {
                using var identity = new WindowsIdentity(token);
                if (identity.User != WindowsIdentity.GetCurrent().User) return "只能关闭当前用户启动的软件。";
            }
            finally { CloseHandle(token); }
            if (force)
            {
                // Do not kill a tree: child processes can belong to another task or contain unsaved work.
                process.Kill(entireProcessTree: false);
                return $"已请求强制结束 {target.Name}（PID {target.Pid}）。";
            }
            return process.CloseMainWindow()
                ? "已请求正常关闭，请在目标软件中处理保存提示。"
                : "目标没有可关闭的主窗口或未接受请求。需要时可单独选择强制结束。";
        }
        catch (ArgumentException) { return "该进程已经退出。"; }
        catch (InvalidOperationException) { return "该进程已经退出或状态发生变化。"; }
        catch (System.ComponentModel.Win32Exception) { return "权限不足，未关闭目标软件。"; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessCritical(IntPtr process, out bool critical);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
