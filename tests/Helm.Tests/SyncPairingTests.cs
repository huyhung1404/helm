using System.Security.Cryptography;
using Helm.Core.Sync;

namespace Helm.Tests;

/// <summary>Pairing by a code (docs/sync-protocol.md, "Pairing"): the code, and the package sealed to the new device.</summary>
public sealed class SyncPairingTests
{
    private static readonly Uri Server = new("https://sync.example/");

    [Fact]
    public void The_code_is_18_digits_named_by_the_public_key()
    {
        using var offer = SyncPairingOffer.Create(Server, "fairy-box");
        Assert.Matches("^[0-9]{18}$", offer.Code);
        Assert.Equal(offer.Code, SyncPairing.CodeOf(offer.PublicKey));
        Assert.True(SyncPairing.Matches(offer.Code, offer.PublicKey));
        Assert.Matches("^[0-9a-f]{64}$", offer.Id);
        Assert.Equal(SyncPairing.IdOf(offer.Code), offer.Id);

        using var other = SyncPairingOffer.Create(Server, "fairy-box");
        Assert.NotEqual(offer.Code, other.Code);
        Assert.False(SyncPairing.Matches(offer.Code, other.PublicKey));
    }

    [Fact]
    public void A_code_reads_back_however_it_was_typed()
    {
        using var offer = SyncPairingOffer.Create(Server, "fairy-box");
        var shown = SyncPairing.FormatCode(offer.Code);
        Assert.Matches("^[0-9]{3}( [0-9]{3}){5}$", shown);
        Assert.Equal(offer.Code, SyncPairing.Normalize(shown));
        Assert.Equal(offer.Code, SyncPairing.Normalize(" " + shown.Replace(' ', '-') + " "));
        Assert.Equal("123456789012345678", SyncPairing.Normalize("123.456.789 012-345-678"));
        Assert.Null(SyncPairing.Normalize("12345678901234567"));
        Assert.Null(SyncPairing.Normalize("1234567890123456789"));
        Assert.Null(SyncPairing.Normalize("12345678901234567O"));
        Assert.Null(SyncPairing.Normalize(null));
    }

    [Fact]
    public void Only_the_offering_device_opens_its_package_for_its_pairing()
    {
        using var offer = SyncPairingOffer.Create(Server, "fairy-box");
        var keys = RandomNumberGenerator.GetBytes(48);
        var sealedPackage = SyncPairing.Seal(offer.PublicKey, offer.Id, new SyncPairingPackage("helm_pat_token", keys));

        var opened = SyncPairing.Open(offer, sealedPackage);
        Assert.Equal("helm_pat_token", opened.Token);
        Assert.Equal(keys, opened.Keys);
        Assert.DoesNotContain("helm_pat_token", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(sealedPackage)));

        using var stranger = SyncPairingOffer.Create(Server, "someone");
        Assert.Throws<SyncKeyException>(() => SyncPairing.Open(stranger, sealedPackage));

        var forOtherPairing = SyncPairing.Seal(offer.PublicKey, stranger.Id, new SyncPairingPackage("helm_pat_token", keys));
        Assert.Throws<SyncKeyException>(() => SyncPairing.Open(offer, forOtherPairing));

        var bytes = Convert.FromBase64String(sealedPackage);
        bytes[^1] ^= 1;
        Assert.Throws<SyncKeyException>(() => SyncPairing.Open(offer, Convert.ToBase64String(bytes)));
        Assert.Throws<SyncKeyException>(() => SyncPairing.Open(offer, "not base64!"));
        Assert.Throws<SyncKeyException>(() => SyncPairing.Open(offer, Convert.ToBase64String([1, 0])));
    }

    [Fact]
    public void A_package_is_sealed_only_to_a_P256_key()
    {
        using var rsa = RSA.Create(2048);
        Assert.Throws<SyncKeyException>(() => SyncPairing.Seal(rsa.ExportSubjectPublicKeyInfo(), "id", new SyncPairingPackage("t", [1])));
        using var p384 = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<SyncKeyException>(() => SyncPairing.Seal(p384.ExportSubjectPublicKeyInfo(), "id", new SyncPairingPackage("t", [1])));
        Assert.Throws<SyncKeyException>(() => SyncPairing.Seal([1, 2, 3], "id", new SyncPairingPackage("t", [1])));
    }
}
