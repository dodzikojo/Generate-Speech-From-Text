using System.ComponentModel;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Credentials;

[SupportedOSPlatform("windows")]
public sealed partial class WindowsCredentialStore : ICredentialStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBlobBytes = 5 * 512;

    public Task<string?> GetAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        var targetName = GetTargetName(providerId);

        if (!CredRead(targetName, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return Task.FromResult<string?>(null);
            }

            throw new Win32Exception(error, "Windows Credential Manager could not read the VoiceBench credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return Task.FromResult<string?>(string.Empty);
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var secret = Encoding.UTF8.GetString(bytes);
            CryptographicOperations.ZeroMemory(bytes);
            return Task.FromResult<string?>(secret);
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public Task SetAsync(
        string providerId,
        string secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        var targetName = GetTargetName(providerId);
        var secretBytes = Encoding.UTF8.GetBytes(secret);

        if (secretBytes.Length > MaximumCredentialBlobBytes)
        {
            throw new ArgumentException(
                $"Credential exceeds the Windows generic credential limit of {MaximumCredentialBlobBytes} bytes.",
                nameof(secret));
        }

        var secretPointer = Marshal.AllocCoTaskMem(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, secretPointer, secretBytes.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = checked((uint)secretBytes.Length),
                CredentialBlob = secretPointer,
                Persist = CredentialPersistLocalMachine,
                UserName = "VoiceBench API key",
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows Credential Manager could not save the VoiceBench credential.");
            }

            return Task.CompletedTask;
        }
        finally
        {
            for (var index = 0; index < secretBytes.Length; index++)
            {
                Marshal.WriteByte(secretPointer, index, 0);
                secretBytes[index] = 0;
            }

            Marshal.FreeCoTaskMem(secretPointer);
        }
    }

    public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        var targetName = GetTargetName(providerId);

        if (!CredDelete(targetName, CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(
                    error,
                    "Windows Credential Manager could not delete the VoiceBench credential.");
            }
        }

        return Task.CompletedTask;
    }

    public static string GetTargetName(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        if (!ProviderIdPattern().IsMatch(providerId))
        {
            throw new ArgumentException(
                "Provider identifiers may contain only letters, numbers, dots, underscores, and hyphens.",
                nameof(providerId));
        }

        return $"IssueLab.VoiceBench/{providerId.ToLowerInvariant()}/ApiKey";
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("VoiceBench uses Windows Credential Manager on Windows.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderIdPattern();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint reservedFlag,
        out IntPtr credentialPointer);

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite([In] ref NativeCredential userCredential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr credentialPointer);
}
