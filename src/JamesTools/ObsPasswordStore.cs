using System.ComponentModel;
using System.Runtime.InteropServices;

namespace JamesTools;

internal static class ObsPasswordStore
{
    private const uint Generic = 1;
    private const uint PersistOnThisPc = 2;
    private const int NotFound = 1168;
    private const int MaxBlobBytes = 2560;
    private const string ConnectOnStartup = "Connect when Meh Tools starts";

    internal sealed class SavedPassword(string password, bool autoConnect)
    {
        public string Password { get; } = password;
        public bool AutoConnect { get; } = autoConnect;
    }

    private static string Target(int port) => FormattableString.Invariant($"Meh Tools/OBS/127.0.0.1:{port}");

    internal static SavedPassword? Read(int port)
    {
        if (!CredRead(Target(port), Generic, 0, out var buffer))
        {
            var error = Marshal.GetLastWin32Error();
            return error == NotFound ? null : throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(buffer);
            if (credential.BlobSize == 0 || credential.BlobSize > MaxBlobBytes || credential.BlobSize % sizeof(char) != 0 || credential.Blob == IntPtr.Zero)
                throw new InvalidDataException("The saved OBS password has an invalid format.");
            var password = Marshal.PtrToStringUni(credential.Blob, (int)credential.BlobSize / sizeof(char))!;
            return new SavedPassword(password, credential.Comment == ConnectOnStartup);
        }
        finally { CredFree(buffer); }
    }

    internal static void Save(int port, string password, bool autoConnect = true)
    {
        if (password.Length is 0 or > MaxBlobBytes / sizeof(char)) throw new ArgumentException("Windows cannot store an OBS password of this length.");
        var blob = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var credential = new Credential
            {
                Type = Generic, TargetName = Target(port), UserName = "OBS WebSocket", Comment = autoConnect ? ConnectOnStartup : null,
                Blob = blob, BlobSize = (uint)(password.Length * sizeof(char)), Persist = PersistOnThisPc
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }

    internal static void PauseAutoConnect(int port)
    {
        if (Read(port) is { AutoConnect: true } saved) Save(port, saved.Password, autoConnect: false);
    }

    internal static void Forget(int port)
    {
        if (CredDelete(Target(port), Generic, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != NotFound) throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
