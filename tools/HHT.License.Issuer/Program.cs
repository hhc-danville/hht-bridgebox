using System.Security.Cryptography;
using HHT.Bridge.Core.Licensing;

// hht-license issue  --key <private.pem> --to "Customer" --edition Free|Pro|Shop [--seats 1] [--expires 2027-12-31]
// hht-license verify <license-key>
// hht-license keygen --out <folder>     (only to start a NEW key pair; the console must then be rebuilt with the new public key)
var a = args.ToList();
string? Opt(string name) { int i = a.IndexOf(name); return i >= 0 && i + 1 < a.Count ? a[i + 1] : null; }

switch (a.FirstOrDefault())
{
    case "issue":
    {
        var keyPath = Opt("--key") ?? throw new ArgumentException("--key private.pem is required");
        var to = Opt("--to") ?? throw new ArgumentException("--to \"Customer name\" is required");
        var edition = Enum.Parse<Edition>(Opt("--edition") ?? "Pro", ignoreCase: true);
        int seats = int.Parse(Opt("--seats") ?? "1");
        DateTime? expires = Opt("--expires") is { } e ? DateTime.Parse(e).Date.AddDays(1).AddSeconds(-1) : null;
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(File.ReadAllText(keyPath));
        var info = new LicenseInfo(Guid.NewGuid().ToString("N")[..12], to, edition, seats, DateTime.UtcNow, expires);
        var key = LicenseVerifier.Issue(info, ecdsa);
        if (!LicenseVerifier.TryVerify(key, DateTime.UtcNow, out _, out var err))
            throw new InvalidOperationException("The private key does not match the public key built into the console: " + err);
        Console.WriteLine(key);
        Console.Error.WriteLine($"Issued {edition} license {info.Id} to {to}, {seats} seat(s){(expires is { } x ? $", expires {x:yyyy-MM-dd}" : ", no expiry")}.");
        return 0;
    }
    case "verify":
    {
        var ok = LicenseVerifier.TryVerify(a.ElementAtOrDefault(1), DateTime.UtcNow, out var info, out var err);
        Console.WriteLine(ok ? $"VALID: {info}" : $"INVALID: {err}");
        return ok ? 0 : 1;
    }
    case "keygen":
    {
        var outDir = Opt("--out") ?? ".";
        Directory.CreateDirectory(outDir);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(Path.Combine(outDir, "hht-license-private.pem"), ecdsa.ExportECPrivateKeyPem());
        File.WriteAllText(Path.Combine(outDir, "hht-license-public.pem"), ecdsa.ExportSubjectPublicKeyInfoPem());
        Console.WriteLine($"New key pair written to {outDir}. Put the PUBLIC key into LicenseVerifier.PublicKeyPem and rebuild; keep the PRIVATE key offline.");
        return 0;
    }
    default:
        Console.WriteLine("usage:\n  hht-license issue --key private.pem --to \"Customer\" --edition Free|Pro|Shop [--seats N] [--expires yyyy-mm-dd]\n  hht-license verify <key>\n  hht-license keygen --out <folder>");
        return 2;
}
