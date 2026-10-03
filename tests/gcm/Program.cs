using System.Security.Cryptography;
using BHDisplay.Core;

int fails = 0;
void Check(bool ok, string name) { if (!ok) { fails++; Console.WriteLine("FAIL: " + name); } else Console.WriteLine("ok   " + name); }
byte[] H(string s) => Convert.FromHexString(s);

// --- NIST GCM spec (McGrew & Viega) test cases 13 and 14: AES-256, zero key, zero IV.
{
    using var g = new AesGcm256(new byte[32]);
    var c13 = g.Seal(new byte[12], Array.Empty<byte>());
    Check(Convert.ToHexStringLower(c13) == "530f8afbc74536b9a963b4f1c4cb738b", "GCM test case 13 (empty)");
    var c14 = g.Seal(new byte[12], new byte[16]);
    Check(Convert.ToHexStringLower(c14) == "cea7403d4d606b6e074ec5d3baf39d18" + "d0d1c8a799996bf0265b98b5d48ab919", "GCM test case 14 (one block)");
}

// --- Differential: our GCM vs .NET's AesGcm, many lengths, random keys/nonces/data.
{
    var rnd = new Random(1);
    bool allSame = true, allOpen = true, tamperRejected = true;
    for (int i = 0; i < 3000; i++)
    {
        int len = i < 300 ? i : rnd.Next(0, 70000);
        var key = RandomNumberGenerator.GetBytes(32); var nonce = RandomNumberGenerator.GetBytes(12); var p = RandomNumberGenerator.GetBytes(len);
        using var ours = new AesGcm256(key);
        using var net = new AesGcm(key, 16);
        var o = ours.Seal(nonce, p);
        var c = new byte[len]; var t = new byte[16];
        net.Encrypt(nonce, p, c, t);
        if (!o.AsSpan().SequenceEqual([.. c, .. t])) allSame = false;
        var back = ours.Open(nonce, o);
        if (back is null || !back.AsSpan().SequenceEqual(p)) allOpen = false;
        var bad = (byte[])o.Clone(); bad[rnd.Next(bad.Length)] ^= (byte)(1 << rnd.Next(8));
        if (ours.Open(nonce, bad) is not null) tamperRejected = false;
    }
    Check(allSame, "GCM output identical to .NET AesGcm (3000 random cases, 0–70000 bytes)");
    Check(allOpen, "GCM opens what it sealed");
    Check(tamperRejected, "GCM rejects any single flipped bit (ciphertext or tag)");
    using var w = new AesGcm256(new byte[32]);
    Check(w.Open(new byte[12], new byte[15]) is null, "GCM rejects a body shorter than the tag");
}

// --- HKDF: RFC 5869 test case 1, and differential vs .NET HKDF.
{
    var prk = Hkdf.Extract(H("000102030405060708090a0b0c"), H("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b"));
    var okm = Hkdf.Expand(prk, H("f0f1f2f3f4f5f6f7f8f9"), 42);
    Check(Convert.ToHexStringLower(okm) == "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865", "HKDF RFC 5869 test case 1");
    bool same = true;
    for (int i = 0; i < 200; i++)
    {
        var ikm = RandomNumberGenerator.GetBytes(32); var salt = RandomNumberGenerator.GetBytes(32); var info = RandomNumberGenerator.GetBytes(i % 20);
        int L = 1 + i % 100;
        if (!Hkdf.Expand(Hkdf.Extract(salt, ikm), info, L).AsSpan().SequenceEqual(HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, L, salt, info))) same = false;
    }
    Check(same, "HKDF identical to .NET HKDF (200 random cases)");
}

// --- Key agreement shortcut: DeriveKeyFromHmac(peer, SHA256, T) == HKDF-Extract(T, raw shared secret).
{
    bool same = true;
    for (int i = 0; i < 50; i++)
    {
        using var a = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var b = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var t = RandomNumberGenerator.GetBytes(32);
        var viaHmac = a.DeriveKeyFromHmac(b.PublicKey, HashAlgorithmName.SHA256, t);
        var viaRaw = Hkdf.Extract(t, a.DeriveRawSecretAgreement(b.PublicKey));
        if (!viaHmac.AsSpan().SequenceEqual(viaRaw)) same = false;
    }
    Check(same, "DeriveKeyFromHmac(T) equals HKDF-Extract(T, Z) (50 key pairs)");
}

// --- ECDSA: SignData without a format gives raw r||s (64 bytes) that the P1363 verifier accepts.
{
    using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var data = RandomNumberGenerator.GetBytes(40);
    var sig = k.SignData(data, HashAlgorithmName.SHA256);
    Check(sig.Length == 64 && k.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
          "ECDSA default signature is raw r||s");
}

// --- Hex helpers.
Check(Bytes.Hex(H("00ff10ab")) == "00ff10ab" && Bytes.FromHex("00FF10ab")!.AsSpan().SequenceEqual(H("00ff10ab")) && Bytes.FromHex("0g") is null, "hex round trip");

Console.WriteLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
return fails == 0 ? 0 : 1;
