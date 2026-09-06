using System.Runtime.InteropServices;
using System.Text.Json;

internal static class ConsoleTestHost
{
    private const uint RequiredErrorMode = 0x0001 | 0x0002 | 0x8000;
    private const uint WerAlwaysShowUI = 0x0010;
    private const uint WerNoUI = 0x0020;

    public static int Run(string[] args, Action<string[]> command)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                SetErrorMode(GetErrorMode() | RequiredErrorMode);
                Marshal.ThrowExceptionForHR(WerGetFlags(new IntPtr(-1), out var flags));
                Marshal.ThrowExceptionForHR(WerSetFlags((flags & ~WerAlwaysShowUI) | WerNoUI));
            }
            if (args is ["--test-host-state"])
            {
                var errorMode = OperatingSystem.IsWindows() ? GetErrorMode() : 0;
                uint werFlags = 0;
                if (OperatingSystem.IsWindows())
                {
                    Marshal.ThrowExceptionForHR(WerGetFlags(new IntPtr(-1), out werFlags));
                    if ((errorMode & RequiredErrorMode) != RequiredErrorMode || (werFlags & WerNoUI) == 0 || (werFlags & WerAlwaysShowUI) != 0)
                        throw new InvalidOperationException("Non-interactive Windows error handling is not configured.");
                }
                Console.WriteLine(JsonSerializer.Serialize(new { Windows = OperatingSystem.IsWindows(), ErrorMode = errorMode, WerFlags = werFlags }));
                return 0;
            }
            if (args is ["--test-host-failure"]) throw new InvalidOperationException("Controlled test-host failure probe.");
            command(args);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Foretell test command failed:");
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetErrorMode();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WerGetFlags(IntPtr process, out uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WerSetFlags(uint flags);
}
