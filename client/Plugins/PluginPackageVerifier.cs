using System.Security.Cryptography;
using NSec.Cryptography;

namespace RobloxAltClient.Plugins;

public interface IPluginPackageSignatureVerifier
{
    bool Verify(ReadOnlySpan<byte> packageBytes, ReadOnlySpan<byte> signatureBytes);
}

/// <summary>Verifies release packages using the pinned raw Ed25519 public key.</summary>
public sealed class PinnedEd25519PackageSignatureVerifier : IPluginPackageSignatureVerifier
{
    private readonly byte[] _rawPublicKey;

    public PinnedEd25519PackageSignatureVerifier(byte[] rawPublicKey)
    {
        if (rawPublicKey is null || rawPublicKey.Length != 32)
            throw new ArgumentException("An Ed25519 public key must contain exactly 32 bytes.", nameof(rawPublicKey));
        _rawPublicKey = rawPublicKey.ToArray();
    }

    public bool Verify(ReadOnlySpan<byte> packageBytes, ReadOnlySpan<byte> signatureBytes)
    {
        try
        {
            if (signatureBytes.Length != SignatureAlgorithm.Ed25519.SignatureSize) return false;
            var key = PublicKey.Import(SignatureAlgorithm.Ed25519, _rawPublicKey, KeyBlobFormat.RawPublicKey);
            return SignatureAlgorithm.Ed25519.Verify(key, packageBytes, signatureBytes);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException or NotSupportedException)
        {
            return false;
        }
    }
}
