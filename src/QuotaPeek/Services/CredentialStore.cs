using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace QuotaPeek.Services;

public sealed class CredentialStore(string scope)
{
    private string Target(string id) => $"QuotaPeek/{scope}/{id}";
    public string? Read(string id)
    {
        if (!CredRead(Target(id), 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error, "无法读取 Windows 凭据管理器。");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally { CredFree(pointer); }
    }

    public void Write(string id, string secret)
    {
        if (Encoding.Unicode.GetByteCount(secret) > 2560 || secret.Contains('\r') || secret.Contains('\n'))
            throw new ArgumentException("API key 长度或格式无效。");
        var pointer = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var credential = new Credential { Type = 1, TargetName = Target(id), CredentialBlob = pointer,
                CredentialBlobSize = (uint)Encoding.Unicode.GetByteCount(secret), Persist = 2, UserName = "QuotaPeek" };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保存 Windows 凭据。");
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(pointer); }
    }

    public void Delete(string id)
    {
        if (!CredDelete(Target(id), 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法删除 Windows 凭据。");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, int flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, int flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr buffer);
}
