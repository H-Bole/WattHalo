using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WattMonitor.Updates;

internal sealed record SignedUpdateManifest(string Product, int Format, string Version, string Archive, long Size, string Sha256);

internal static class UpdateManifest
{
    internal const string ArchiveName = "WattHalo-update.zip";
    internal const string ManifestName = "WattHalo-update.json";
    internal const string SignatureName = "WattHalo-update.sig";
    internal const long MaximumArchiveSize = 1L << 30;

    internal static SignedUpdateManifest Verify(byte[] content, byte[] signature)
    {
        if (content.Length is 0 or > 16384 || signature.Length is 0 or > 1024)
            throw new InvalidDataException("更新签名或清单大小无效。");
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("WattMonitor.UpdatePublicKey.pem")
            ?? throw new InvalidDataException("未配置发布者公钥，无法验证更新。");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(reader.ReadToEnd());
            if (rsa.KeySize < 3072 || !rsa.VerifyData(content, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidDataException("更新签名验证失败。");
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException) { throw new InvalidDataException("更新公钥或签名无效。", ex); }
        var manifest = JsonSerializer.Deserialize<SignedUpdateManifest>(content)
            ?? throw new InvalidDataException("更新清单为空。");
        if (manifest.Product != "WattMonitor" || manifest.Format != 1 || manifest.Archive != ArchiveName ||
            !TryVersion(manifest.Version, out _) || manifest.Size is <= 0 or > MaximumArchiveSize ||
            manifest.Sha256 is null || !Regex.IsMatch(manifest.Sha256, "^[0-9A-Fa-f]{64}$"))
            throw new InvalidDataException("更新清单内容无效。");
        return manifest;
    }

    internal static bool TryVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        return text is not null && Regex.IsMatch(text, @"^(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})$") &&
            Version.TryParse(text, out version!);
    }

    internal static void VerifyArchive(string path, SignedUpdateManifest manifest)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != manifest.Size || !CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(stream), Convert.FromHexString(manifest.Sha256)))
            throw new InvalidDataException("更新包大小或 SHA-256 不匹配。");
    }
}
