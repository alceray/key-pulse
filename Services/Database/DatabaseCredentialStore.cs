using System.ComponentModel;
using System.Runtime.InteropServices;
using KeyPulse.Configuration;
using KeyPulse.Models;

namespace KeyPulse.Services;

public interface IDatabaseCredentialStore
{
    string? ReadPostgreSqlPassword(string? credentialReference = null);
    void WritePostgreSqlPassword(string password, string? credentialReference = null);
    void DeletePostgreSqlPassword(string? credentialReference = null);
}

public sealed class WindowsDatabaseCredentialStore : IDatabaseCredentialStore
{
    private const int CredentialTypeGeneric = 1;
    private const int CredentialPersistLocalMachine = 2;

    internal static string TargetName(string? reference)
    {
        var prefix = $"{AppConstants.App.PostgreSqlCredentialPrefix}/{BuildInfo.EnvironmentName}";
        if (reference == null)
            return prefix;
        if (!Guid.TryParseExact(reference, "N", out _))
            throw new InvalidOperationException("The saved database credential reference is invalid");
        return $"{prefix}/{reference}";
    }

    public string? ReadPostgreSqlPassword(string? credentialReference = null)
    {
        if (!CredRead(TargetName(credentialReference), CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) // ERROR_NOT_FOUND
                return null;
            throw new Win32Exception(error, "Unable to read the saved database credential");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            return credential.CredentialBlob == IntPtr.Zero
                ? string.Empty
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void WritePostgreSqlPassword(string password, string? credentialReference = null)
    {
        ArgumentNullException.ThrowIfNull(password);
        var blob = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = TargetName(credentialReference),
                CredentialBlobSize = (uint)(password.Length * sizeof(char)),
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = Environment.UserName,
            };

            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to save the database credential");
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    public void DeletePostgreSqlPassword(string? credentialReference = null)
    {
        if (CredDelete(TargetName(credentialReference), CredentialTypeGeneric, 0))
            return;

        var error = Marshal.GetLastWin32Error();
        if (error != 1168)
            throw new Win32Exception(error, "Unable to delete the saved database credential");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPointer);

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
