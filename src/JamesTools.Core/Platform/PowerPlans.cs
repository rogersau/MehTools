using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace JamesTools.Core.Platform;

public sealed record PowerPlan(Guid Id, string Name)
{
    public override string ToString() => Name;
}

public static class PowerPlans
{
    private const uint AccessScheme = 16;
    private const uint ErrorNoMoreItems = 259;
    private const uint ErrorMoreData = 234;

    public static IReadOnlyList<PowerPlan> List()
    {
        var plans = new List<PowerPlan>();
        for (uint index = 0; ; index++)
        {
            var id = Guid.Empty;
            uint size = 16;
            var status = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, index, ref id, ref size);
            if (status == ErrorNoMoreItems)
                break;
            ThrowIfError(status, "enumerate power plans");
            plans.Add(new PowerPlan(id, ReadName(id)));
        }

        return plans;
    }

    public static Guid GetActive()
    {
        var status = PowerGetActiveScheme(IntPtr.Zero, out var pointer);
        ThrowIfError(status, "get the active power plan");
        if (pointer == IntPtr.Zero)
            throw new InvalidOperationException("Windows returned no active power plan.");

        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    public static void SetActive(Guid id)
    {
        ThrowIfError(PowerSetActiveScheme(IntPtr.Zero, ref id), "set the active power plan");
        if (GetActive() != id)
            throw new InvalidOperationException("Windows did not activate the selected power plan.");
    }

    private static string ReadName(Guid id)
    {
        uint size = 0;
        ThrowIfError(PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, null, ref size), "read a power plan name");
        if (size == 0)
            return id.ToString();

        // A name can change between calls; retry with the newly reported size.
        while (true)
        {
            var bytes = new byte[checked((int)size)];
            var status = PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, bytes, ref size);
            if (status == ErrorMoreData)
                continue;
            ThrowIfError(status, "read a power plan name");
            return Encoding.Unicode.GetString(bytes, 0, checked((int)size)).TrimEnd('\0');
        }
    }

    private static void ThrowIfError(uint status, string operation)
    {
        if (status != 0)
            throw new Win32Exception(unchecked((int)status), $"Could not {operation}: {new Win32Exception(unchecked((int)status)).Message}");
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr subgroup, uint access, uint index, ref Guid buffer, ref uint size);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, [Out] byte[]? buffer, ref uint size);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr activeScheme);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
