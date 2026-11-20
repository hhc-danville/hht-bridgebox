using System.Security.Cryptography;
using System.Text;

namespace HHT.Bridge.TechConsole.Security;

/// <summary>Windows DPAPI (current user) for secrets at rest: shop access key, module token, TLS private key.</summary>
public static class Dpapi
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HHT.BridgeConsole.v1");

    public static byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    public static byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);

    public static string? ProtectString(string? value) =>
        string.IsNullOrEmpty(value) ? null : Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(value)));

    public static string? UnprotectString(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try { return Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(stored))); }
        catch (Exception) { return null; } // other user / corrupted: treat as not set
    }
}
