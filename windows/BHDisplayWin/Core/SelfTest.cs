// `BHDisplay.exe --selftest`: checks this machine's crypto against known answers without touching the real
// identity or pairing — the parts no test on another OS can cover (Windows CNG and .NET Framework).
using System.Security.Cryptography;
using System.Text;

namespace BHDisplay.Core;

public static class SelfTest
{
    public static (bool Ok, string Report) Run()
    {
        var sb = new StringBuilder();
        bool all = true;
        void Check(string name, Func<bool> test)
        {
            bool ok;
            try { ok = test(); } catch (Exception e) { ok = false; name += " — " + e.GetType().Name + ": " + e.Message; }
            all &= ok;
            sb.AppendLine((ok ? "ok    " : "FAIL  ") + name);
        }

        Check("AES-GCM known answers (NIST test cases 13, 14)", () =>
        {
            using var g = new AesGcm256(new byte[32]);
            return Bytes.Hex(g.Seal(new byte[12], Array.Empty<byte>())) == "530f8afbc74536b9a963b4f1c4cb738b"
                && Bytes.Hex(g.Seal(new byte[12], new byte[16])) == "cea7403d4d606b6e074ec5d3baf39d18d0d1c8a799996bf0265b98b5d48ab919";
        });
        Check("AES-GCM rejects a tampered message", () =>
        {
            using var g = new AesGcm256(Bytes.Random(32));
            var n = Bytes.Random(12); var c = g.Seal(n, Bytes.Random(100));
            c[5] ^= 1;
            return g.Open(n, c) is null;
        });
        Check("HKDF known answer (RFC 5869 test case 1)", () =>
        {
            var prk = Hkdf.Extract(Bytes.FromHex("000102030405060708090a0b0c")!, Bytes.FromHex("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b")!);
            return Bytes.Hex(Hkdf.Expand(prk, Bytes.FromHex("f0f1f2f3f4f5f6f7f8f9")!, 42))
                == "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865";
        });
        Check("Key agreement gives both sides the same keys and pairing code", () =>
        {
            using var a = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            using var b = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var t = Bytes.Random(32);
            var ka = Handshake.DeriveKeys(a, P256.Export(b.ExportParameters(false)), t, isDialer: true);
            var kb = Handshake.DeriveKeys(b, P256.Export(a.ExportParameters(false)), t, isDialer: false);
            return Bytes.Equal(ka.Send, kb.Receive) && Bytes.Equal(ka.Receive, kb.Send) && ka.PairCode == kb.PairCode
                && !Bytes.Equal(ka.Send, ka.Receive);
        });
        Check("A new identity can be created, saved and loaded back", () =>
        {
            byte[]? saved = null;
            var id1 = ShareIdentity.LoadOrCreate(() => null, b => saved = b, "selftest");
            if (saved is null || saved.Length != 112) return false;
            var id2 = ShareIdentity.LoadOrCreate(() => saved, _ => throw new Exception("must not re-create"), "selftest");
            return Bytes.Equal(id1.PublicKey, id2.PublicKey) && Bytes.Equal(id1.DeviceId, id2.DeviceId);
        });
        Check("Signatures are 64-byte r||s and verify; a wrong role fails", () =>
        {
            var id = ShareIdentity.Ephemeral("selftest");
            var t = Bytes.Random(32);
            var sig = Handshake.Sign(id, 0, t);
            return sig.Length == 64 && Handshake.Verify(sig, id.PublicKey, 0, t) && !Handshake.Verify(sig, id.PublicKey, 1, t);
        });
        Check("A point that is not on the curve is refused", () =>
        {
            var bad = new byte[65]; bad[0] = 4; bad[64] = 1;
            try { P256.Import(bad); return false; } catch (Exception) { return true; }
        });
        return (all, sb.ToString());
    }
}
