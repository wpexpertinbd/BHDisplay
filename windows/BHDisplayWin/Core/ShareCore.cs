// Keyboard & mouse sharing — wire format, identity, handshake and record encryption.
// Implements docs/SHARING-PROTOCOL.md; must stay byte-for-byte compatible with Sources/ShareCore.swift.
using System.Security.Cryptography;
using System.Text;

namespace BHDisplay.Core;

public static class Bhds
{
    public const int TcpPort = 24860;
    public const int UdpPort = 24861;
    public const int MaxFrame = 1_048_576;
    public const int MaxClipboard = 256 * 1024;
    public const byte Version = 2;

    /// A peer-supplied name for logs and dialogs: no control or invisible formatting characters (newlines,
    /// right-to-left overrides) that could forge log lines or disguise the name.
    public static string CleanName(string s)
    {
        var t = new string(s.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray()).Trim();
        return t.Length == 0 ? "unnamed computer" : t;
    }
    public static readonly byte[] HelloMagic = Bytes.Ascii("BHDS");
    public static readonly byte[] BeaconMagic = Bytes.Ascii("BHDS1");
}

public sealed class WireException(string message) : Exception(message);

public sealed class WireWriter
{
    private readonly MemoryStream _s = new();
    public void U8(byte v) => _s.WriteByte(v);
    public void U16(ushort v) { _s.WriteByte((byte)(v >> 8)); _s.WriteByte((byte)v); }
    public void U32(uint v) { var b = new byte[4]; BHDisplay.Core.Bytes.PutU32(b, 0, v); _s.Write(b, 0, 4); }
    public void I16(short v) => U16(unchecked((ushort)v));
    public void F32(float v) => U32(BitConverter.ToUInt32(BitConverter.GetBytes(v), 0));
    public void Bytes(byte[] b) => _s.Write(b, 0, b.Length);
    public void Bytes(byte[] b, int count) => _s.Write(b, 0, count);
    public byte[] ToArray() => _s.ToArray();
}

public sealed class WireReader(byte[] data)
{
    private readonly byte[] _d = data;
    private int _o = 0;
    public int Remaining => _d.Length - _o;
    public byte[] Bytes(int n)
    {
        if (n < 0 || Remaining < n) throw new WireException("truncated message");
        var s = BHDisplay.Core.Bytes.Slice(_d, _o, n); _o += n; return s;
    }
    public byte U8() => Bytes(1)[0];
    public ushort U16() { var b = Bytes(2); return (ushort)(b[0] << 8 | b[1]); }
    public uint U32() => BHDisplay.Core.Bytes.GetU32(Bytes(4), 0);
    public short I16() => unchecked((short)U16());
    public float F32() => BitConverter.ToSingle(BitConverter.GetBytes(U32()), 0);
    public byte[] Rest() => Bytes(Remaining);
}

public static class P256
{
    /// X9.63 uncompressed (0x04 || X || Y, 65 bytes) — the encoding CryptoKit uses.
    public static byte[] Export(ECParameters p)
    {
        var o = new byte[65]; o[0] = 4;
        p.Q.X!.CopyTo(o, 1); p.Q.Y!.CopyTo(o, 33);
        return o;
    }
    public static ECParameters Import(byte[] x963)
    {
        if (x963.Length != 65 || x963[0] != 4) throw new WireException("not an uncompressed P-256 point");
        var p = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = Bytes.Slice(x963, 1, 32), Y = Bytes.Slice(x963, 33, 32) },
        };
        p.Validate();
        // Validate() checks sizes only; importing makes the platform reject points not on the curve.
        using var probe = ECDsa.Create(p);
        return p;
    }
    public static string Hex(byte[] b) => Bytes.Hex(b);
}

/// Long-term identity. Storage is supplied by the host (DPAPI file on Windows, plain file in tests).
public sealed class ShareIdentity
{
    public byte[] DeviceId { get; }
    public ECDsa SigningKey { get; }
    public string Name { get; }
    public byte[] PublicKey { get; }
    public byte[] Fingerprint => Bytes.Sha256(PublicKey);

    private ShareIdentity(byte[] id, ECDsa key, string name)
    {
        DeviceId = id; SigningKey = key; Name = name.Length > 64 ? name.Substring(0, 64) : name;
        PublicKey = P256.Export(key.ExportParameters(false));
    }

    /// Blob = device id (16) || private scalar D (32) || public X (32) || Y (32). Only this platform reads it.
    public static ShareIdentity LoadOrCreate(Func<byte[]?> load, Action<byte[]> save, string name, Action<string>? log = null)
    {
        var blob = load();
        if (blob is { Length: 112 })
        {
            try
            {
                var key = ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256, D = Bytes.Slice(blob, 16, 32),
                    Q = new ECPoint { X = Bytes.Slice(blob, 48, 32), Y = Bytes.Slice(blob, 80, 32) },
                });
                return new ShareIdentity(Bytes.Slice(blob, 0, 16), key, name);
            }
            catch (CryptographicException e) { log?.Invoke("saved identity couldn't be read (" + e.Message + "); creating a new one"); }
        }
        var k = NewSigningKey();
        var id = Bytes.Random(16);
        var p = k.ExportParameters(true);
        save(Bytes.Concat(id, p.D!, p.Q.X!, p.Q.Y!));
        return new ShareIdentity(id, k, name);
    }

    /// A new P-256 key whose private part can be exported once to be saved. On .NET Framework (Windows CNG) a key
    /// must be created with the plaintext-export policy for that; modern .NET's ECDsa.Create already allows it.
    private static ECDsa NewSigningKey()
    {
#if NETFRAMEWORK
        var p = new CngKeyCreationParameters { ExportPolicy = CngExportPolicies.AllowPlaintextExport, KeyUsage = CngKeyUsages.Signing };
        return new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, null, p));
#else
        return ECDsa.Create(ECCurve.NamedCurves.nistP256);
#endif
    }

    public static ShareIdentity Ephemeral(string name) =>
        new(Bytes.Random(16), ECDsa.Create(ECCurve.NamedCurves.nistP256), name);
}

public sealed record Hello(byte[] DeviceId, byte[] IdentityKey, byte[] EphemeralKey, byte[] Nonce, string Name)
{
    public byte[] Encode()
    {
        var w = new WireWriter();
        w.Bytes(Bhds.HelloMagic); w.U8(Bhds.Version);
        w.Bytes(DeviceId); w.Bytes(IdentityKey); w.Bytes(EphemeralKey); w.Bytes(Nonce);
        var n = Encoding.UTF8.GetBytes(Name);
        int len = Math.Min(n.Length, 64);
        w.U8((byte)len); w.Bytes(n, len);
        return w.ToArray();
    }

    public static Hello Decode(byte[] d)
    {
        var r = new WireReader(d);
        if (!r.Bytes(4).SequenceEqual(Bhds.HelloMagic)) throw new WireException("not a BHDisplay peer");
        if (r.U8() != Bhds.Version) throw new WireException("unsupported protocol version");
        var id = r.Bytes(16); var ik = r.Bytes(65); var ek = r.Bytes(65);
        var nonce = r.Bytes(32);
        int n = r.U8();
        if (n > 64) throw new WireException("name too long");
        var name = Bhds.CleanName(Encoding.UTF8.GetString(r.Bytes(n)));
        if (r.Remaining != 0) throw new WireException("trailing bytes in HELLO");
        P256.Import(ik); P256.Import(ek);   // both must be real curve points
        return new Hello(id, ik, ek, nonce, name);
    }
}

public sealed record HandshakeKeys(byte[] Send, byte[] Receive, string PairCode);

public static class Handshake
{
    public static byte[] Transcript(byte[] dialerHello, byte[] listenerHello) =>
        Bytes.Sha256(Bytes.Ascii("BHDS-v2"), dialerHello, listenerHello);

    /// The listener's commitment to its HELLO, sent before it sees the dialer's: neither side can then choose
    /// its HELLO to steer the pairing code (an attacker in the middle could otherwise make both codes match).
    public static byte[] Commitment(byte[] listenerHello) => Bytes.Sha256(Bytes.Ascii("BHDS-v2 commit"), listenerHello);

    static byte[] AuthMessage(byte role, byte[] t) => Bytes.Concat(Bytes.Ascii("BHDS-auth"), new[] { role }, t);

    /// ECDSA-P256-SHA256, raw r || s (IEEE P1363, 64 bytes) — CryptoKit's rawRepresentation. (SignData without a
    /// format argument produces exactly that on both .NET Framework and modern .NET.)
    public static byte[] Sign(ShareIdentity id, byte role, byte[] t)
    {
        var sig = id.SigningKey.SignData(AuthMessage(role, t), HashAlgorithmName.SHA256);
        if (sig.Length != 64) throw new CryptographicException("unexpected signature format");
        return sig;
    }

    public static bool Verify(byte[] sig, byte[] identityKey, byte role, byte[] t)
    {
        if (sig.Length != 64) return false;
        try
        {
            using var k = ECDsa.Create(P256.Import(identityKey));
            return k.VerifyData(AuthMessage(role, t), sig, HashAlgorithmName.SHA256);
        }
        catch (Exception) { return false; }
    }

    public static HandshakeKeys DeriveKeys(ECDiffieHellman ephemeral, byte[] peerEphemeral, byte[] t, bool isDialer)
    {
        using var peer = ECDiffieHellman.Create(P256.Import(peerEphemeral));
        // HKDF-Extract(salt = T, IKM = Z) is HMAC-SHA256(T, Z): the platform computes it from the shared secret
        // directly (.NET Framework can't hand out the raw secret). Same result as CryptoKit's HKDF(Z, salt: T).
        var prk = ephemeral.DeriveKeyFromHmac(peer.PublicKey, HashAlgorithmName.SHA256, t);
        var k = Hkdf.Expand(prk, Bytes.Ascii("BHDS-v2 keys"), 64);
        var p = Hkdf.Expand(prk, Bytes.Ascii("BHDS-v2 pair"), 4);
        Bytes.Zero(prk);
        var code = Bytes.GetU32(p, 0) % 1_000_000;
        var d2l = Bytes.Slice(k, 0, 32); var l2d = Bytes.Slice(k, 32, 32);
        Bytes.Zero(k);
        return new HandshakeKeys(isDialer ? d2l : l2d, isDialer ? l2d : d2l, code.ToString("D6"));
    }
}

/// AES-256-GCM per direction; nonce = 4 zero bytes || 64-bit big-endian sequence number.
public sealed class RecordCipher(byte[] key) : IDisposable
{
    private readonly AesGcm256 _aes = new(key);
    private ulong _seq;

    private byte[] NextNonce()
    {
        var n = new byte[12];
        Bytes.PutU64(n, 4, _seq++);
        return n;
    }
    public byte[] Seal(byte[] plain) => _aes.Seal(NextNonce(), plain);
    public byte[] Open(byte[] body)
    {
        if (body.Length < 16) throw new WireException("truncated message");
        return _aes.Open(NextNonce(), body) ?? throw new WireException("decryption failed");
    }
    public void Dispose() => _aes.Dispose();
}

public abstract record ShareMsg
{
    public sealed record Ping : ShareMsg; public sealed record Pong : ShareMsg;
    public sealed record PairConfirm : ShareMsg; public sealed record PairReject : ShareMsg;
    public sealed record Enter(byte Edge, float Position) : ShareMsg;
    public sealed record Leave(byte Edge, float Position) : ShareMsg;
    public sealed record Move(short Dx, short Dy) : ShareMsg;
    public sealed record Button(byte Number, bool Down) : ShareMsg;
    public sealed record Scroll(short Dx, short Dy) : ShareMsg;
    public sealed record Key(ushort Usage, bool Down) : ShareMsg;
    public sealed record ReleaseAll : ShareMsg;
    public sealed record Clipboard(string Text) : ShareMsg;
    public sealed record MonitorPorts(byte Mac, byte Other) : ShareMsg;
    public sealed record MonitorShows(byte Code) : ShareMsg;
    public sealed record SwitchRequest(byte Code) : ShareMsg;
    public sealed record SwitchAccepted(byte Code) : ShareMsg;
    public sealed record Unknown(byte Type) : ShareMsg;

    /// Messages that act on the receiving computer — only honoured from a paired peer.
    public bool CarriesInput => this is Enter or Leave or Move or Button or Scroll or Key or Clipboard;

    public byte[] Encode()
    {
        var w = new WireWriter();
        switch (this)
        {
            case Ping: w.U8(0x01); break;
            case Pong: w.U8(0x02); break;
            case PairConfirm: w.U8(0x05); break;
            case PairReject: w.U8(0x06); break;
            case Enter e: w.U8(0x10); w.U8(e.Edge); w.F32(e.Position); break;
            case Leave l: w.U8(0x11); w.U8(l.Edge); w.F32(l.Position); break;
            case Move m: w.U8(0x20); w.I16(m.Dx); w.I16(m.Dy); break;
            case Button b: w.U8(0x21); w.U8(b.Number); w.U8(b.Down ? (byte)1 : (byte)0); break;
            case Scroll s: w.U8(0x22); w.I16(s.Dx); w.I16(s.Dy); break;
            case Key k: w.U8(0x30); w.U16(k.Usage); w.U8(k.Down ? (byte)1 : (byte)0); break;
            case ReleaseAll: w.U8(0x31); break;
            case Clipboard c:
                w.U8(0x40);
                var t = Encoding.UTF8.GetBytes(c.Text);
                w.Bytes(t, Math.Min(t.Length, Bhds.MaxClipboard));
                break;
            case MonitorPorts p: w.U8(0x50); w.U8(p.Mac); w.U8(p.Other); break;
            case MonitorShows m: w.U8(0x51); w.U8(m.Code); break;
            case SwitchRequest q: w.U8(0x52); w.U8(q.Code); break;
            case SwitchAccepted a: w.U8(0x53); w.U8(a.Code); break;
            case Unknown u: w.U8(u.Type); break;
        }
        return w.ToArray();
    }

    public static ShareMsg Decode(byte[] d)
    {
        var r = new WireReader(d);
        var t = r.U8();
        switch (t)
        {
            case 0x01: return new Ping();
            case 0x02: return new Pong();
            case 0x05: return new PairConfirm();
            case 0x06: return new PairReject();
            case 0x10 or 0x11:
            {
                var e = r.U8(); var p = r.F32();
                if (e > 4 || !Num.IsFinite(p)) throw new WireException("bad edge");   // 4 = take over, no edge
                p = Num.Clamp(p, 0f, 1f);
                return t == 0x10 ? new Enter(e, p) : new Leave(e, p);
            }
            case 0x20: return new Move(r.I16(), r.I16());
            case 0x21:
            {
                var b = r.U8(); var down = r.U8() != 0;
                if (b is < 1 or > 5) throw new WireException("bad button");
                return new Button(b, down);
            }
            case 0x22: return new Scroll(r.I16(), r.I16());
            case 0x30: return new Key(r.U16(), r.U8() != 0);
            case 0x31: return new ReleaseAll();
            case 0x40:
            {
                var raw = r.Rest();
                if (raw.Length > Bhds.MaxClipboard) throw new WireException("clipboard too large");
                return new Clipboard(Encoding.UTF8.GetString(raw));
            }
            case 0x50: return new MonitorPorts(r.U8(), r.U8());
            case 0x51: return new MonitorShows(r.U8());
            case 0x52: return new SwitchRequest(r.U8());
            case 0x53: return new SwitchAccepted(r.U8());
            default: return new Unknown(t);
        }
    }
}
