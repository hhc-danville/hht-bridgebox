using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HHT.Bridge.Core.Licensing;

public enum Edition { Free = 0, Pro = 1, Shop = 2 }

public enum Feature
{
    Video, Control, Screenshots,                                                       // Free
    Reports, Recording, Ocr, BiosEntry, Macros, Alerts, Playbooks, TimeTracking, BiosCompare, SignOff, // Pro
    RemoteView, Dashboard, Hardware, BootTests, RmmSync, Ai, CloudRelay, AudioBeep,    // Shop
}

public sealed record LicenseInfo(string Id, string LicensedTo, Edition Edition, int Seats, DateTime Issued, DateTime? Expires);

/// <summary>
/// Offline license keys: "HHT1.&lt;base64url json&gt;.&lt;base64url ECDSA P-256 signature&gt;".
/// Keys are issued with Hammer Hill's private key (tools/HHT.License.Issuer); the console only holds the
/// public key below, so keys can't be forged. No key = Free edition. Seats are informational until a
/// server-side activation check exists.
/// </summary>
public static class LicenseVerifier
{
    public const string Prefix = "HHT1.";

    /// <summary>Hammer Hill Technologies license-signing public key (P-256).</summary>
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEEoROdRFjyi0RxG2Zc23UA5LTzVj9
        zTGxvKPXvc3gnOTCJqIXOkqosM8d+e+M8nYIDDPblR9ymL4PXpupwcMK0A==
        -----END PUBLIC KEY-----
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<Feature, Edition> Minimum = new()
    {
        [Feature.Video] = Edition.Free, [Feature.Control] = Edition.Free, [Feature.Screenshots] = Edition.Free,
        [Feature.Reports] = Edition.Pro, [Feature.Recording] = Edition.Pro, [Feature.Ocr] = Edition.Pro, [Feature.BiosEntry] = Edition.Pro,
        [Feature.Macros] = Edition.Pro, [Feature.Alerts] = Edition.Pro, [Feature.Playbooks] = Edition.Pro, [Feature.TimeTracking] = Edition.Pro,
        [Feature.BiosCompare] = Edition.Pro, [Feature.SignOff] = Edition.Pro,
        [Feature.RemoteView] = Edition.Shop, [Feature.Dashboard] = Edition.Shop, [Feature.Hardware] = Edition.Shop, [Feature.BootTests] = Edition.Shop,
        [Feature.RmmSync] = Edition.Shop, [Feature.Ai] = Edition.Shop, [Feature.CloudRelay] = Edition.Shop, [Feature.AudioBeep] = Edition.Shop,
    };

    public static Edition RequiredFor(Feature f) => Minimum.TryGetValue(f, out var e) ? e : Edition.Shop;
    public static bool Allows(Edition edition, Feature f) => edition >= RequiredFor(f);

    public static string Issue(LicenseInfo info, ECDsa privateKey)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(info, Json);
        var sig = privateKey.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Prefix + B64(payload) + "." + B64(sig);
    }

    public static bool TryVerify(string? key, DateTime now, out LicenseInfo? info, out string error, string? publicKeyPem = null)
    {
        info = null;
        error = "";
        key = (key ?? "").Trim().Replace("\r", "").Replace("\n", "").Replace(" ", "");
        if (key.Length == 0) { error = "No license key."; return false; }
        if (!key.StartsWith(Prefix, StringComparison.Ordinal)) { error = "Not an HHT license key."; return false; }
        var parts = key[Prefix.Length..].Split('.');
        if (parts.Length != 2) { error = "License key is damaged."; return false; }
        byte[] payload, sig;
        try { payload = UnB64(parts[0]); sig = UnB64(parts[1]); }
        catch (FormatException) { error = "License key is damaged."; return false; }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(publicKeyPem ?? PublicKeyPem);
        if (!ecdsa.VerifyData(payload, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            error = "License key signature is not valid.";
            return false;
        }
        try { info = JsonSerializer.Deserialize<LicenseInfo>(payload, Json); }
        catch (JsonException) { info = null; }
        if (info == null) { error = "License key is damaged."; return false; }
        if (info.Expires is { } exp && now > exp) { error = $"License expired on {exp:yyyy-MM-dd}."; info = null; return false; }
        return true;
    }

    /// <summary>Edition in effect: the licensed edition, or Free if the key is missing/invalid/expired.</summary>
    public static Edition EffectiveEdition(string? key, DateTime now, out string status)
    {
        if (TryVerify(key, now, out var info, out var error) && info != null)
        {
            status = $"{info.Edition} edition — licensed to {info.LicensedTo}" + (info.Expires is { } e ? $", valid until {e:yyyy-MM-dd}" : "") + $" ({info.Seats} seat(s)).";
            return info.Edition;
        }
        status = string.IsNullOrWhiteSpace(key) ? "Free edition (no license key)." : $"Free edition — {error}";
        return Edition.Free;
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] UnB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
