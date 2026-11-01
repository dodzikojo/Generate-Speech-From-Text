using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace IssueLab.VoiceBench.Application.Bridge;

internal static class LocalControlSecurity
{
    internal static NamedPipeServerStream CreateServer(string pipeName, bool firstInstance = false)
    {
        // Deny remote clients in the kernel, and grant only the current Windows SID access.
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(A;;GA;;;{sid})", 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), SecurityDescriptor = descriptor };
            var handle = CreateNamedPipe(@"\\.\pipe\" + pipeName, 0x00000003 | 0x40000000 | (firstInstance ? 0x00080000u : 0),
                0x00000008, 16, 65536, 65536, 0, ref attributes);
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally { LocalFree(descriptor); }
    }

    internal static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return TokenElevation(identity.AccessToken);
    }

    internal static void VerifyClient(NamedPipeServerStream pipe)
    {
        using var owner = WindowsIdentity.GetCurrent();
        var expectedSid = owner.User;
        var expectedElevation = TokenElevation(owner.AccessToken);
        var verified = false;
        pipe.RunAsClient(() =>
        {
            using var client = WindowsIdentity.GetCurrent(true);
            verified = client is not null && client.User == expectedSid && TokenElevation(client.AccessToken) == expectedElevation;
        });
        if (!verified) throw new UnauthorizedAccessException("Local control requires the same Windows user and elevation level.");
    }

    internal static void VerifyServer(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var process = OpenProcess(0x1000, false, processId);
        if (process.IsInvalid || !OpenProcessToken(process, 8, out var token)) throw new UnauthorizedAccessException("Cannot verify the desktop process.");
        using (token)
        using (var server = new WindowsIdentity(token.DangerousGetHandle()))
        using (var self = WindowsIdentity.GetCurrent())
        {
            if (server.User != self.User || TokenElevation(token) != TokenElevation(self.AccessToken))
                throw new UnauthorizedAccessException("Local control requires the same Windows user and elevation level.");
        }
    }

    private static bool TokenElevation(SafeAccessTokenHandle token)
    {
        if (!GetTokenInformation(token, 20, out var elevated, sizeof(int), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return elevated != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr SecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint instances, uint outputSize, uint inputSize, uint timeout, ref SecurityAttributes attributes);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out int information, int length, out int returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
