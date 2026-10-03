// Keyboard & mouse sharing — wire format, identity, handshake and record encryption.
// Implements docs/SHARING-PROTOCOL.md; must stay byte-for-byte compatible with Sources/ShareCore.swift.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BHDisplay.Core;

public static class Bhds
{
    public const int TcpPort = 24860;
    public const int UdpPort = 24861;
    public const int MaxFrame = 1_048_576;
    public const int MaxClipboard = 256 * 1024;
    public const byte Version = 1;
    public static readonly byte[] HelloMagic = "BHDS"u8.ToArray();
    public static readonly byte[] BeaconMagic = "BHDS1"u8.ToArray();
}

public sealed class WireException(string message) : Exception(message);

public sealed class WireWriter
{
    private readonly MemoryStream _s = new();
    public void U8(byte v) => _s.WriteByte(v);
    public void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); _s.Write(b); }
    public void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); _s.Write(b); }
    public void I16(short v) => U16(unchecked((ushort)v));
    public void F32(float v) => U32(BitConverter.SingleToUInt32Bits(v));
    public void Bytes(ReadOnlySpan<byte> b) => _s.Write(b);
    public byte[] ToArray() => _s.ToArray();
}

public ref struct WireReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _d = data;
    private int _o = 0;
    public readonly int Remaining => _d.Length - _o;
    public ReadOnlySpan<byte> Bytes(int n)
    {
        if (n < 0 || Remaining < n) throw new WireException("truncated message");
        var s = _d.Slice(_o, n); _o += n; return s;
    }
    public byte U8() => Bytes(1)[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Bytes(2));
    public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Bytes(4));
    public short I16() => unchecked((short)U16());
    public float F32() => BitConverter.UInt32BitsToSingle(U32());
    public ReadOnlySpan<byte> Rest() => Bytes(Remaining);
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
    public static ECParameters Import(ReadOnlySpan<byte> x963)
    {
        if (x963.Length != 65 || x963[0] != 4) throw new WireException("not an uncompressed P-256 point");
        var p = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x963.Slice(1, 32).ToArray(), Y = x963.Slice(33, 32).ToArray() },
        };
        p.Validate();
        // Validate() checks sizes only; importing makes the platform reject points not on the curve.
        using var probe = ECDsa.Create(p);
        return p;
    }
    public static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(b);
}

/// Long-term identity. Storage is supplied by the host (DPAPI file on Windows, plain file in tests).
public sealed class ShareIdentity
{
    public byte[] DeviceId { get; }
    public ECDsa SigningKey { get; }
    public string Name { get; }
    public byte[] PublicKey { get; }
    public byte[] Fingerprint => SHA256.HashData(PublicKey);

    private ShareIdentity(byte[] id, ECDsa key, string name)
    {
        DeviceId = id; SigningKey = key; Name = name.Length > 64 ? name[..64] : name;
        PublicKey = P256.Export(key.ExportParameters(false));
    }

    /// Blob = device id (16) || private scalar D (32) || public X (32) || Y (32). Only this platform reads it.
    public static ShareIdentity LoadOrCreate(Func<byte[]?> load, Action<byte[]> save, string name)
    {
        var blob = load();
        if (blob is { Length: 112 })
        {
            try
            {
                var key = ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256, D = blob[16..48],
                    Q = new ECPoint { X = blob[48..80], Y = blob[80..112] },
                });
                return new ShareIdentity(blob[..16], key, name);
            }
            catch (CryptographicException) { /* corrupt → recreate */ }
        }
        var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var id = RandomNumberGenerator.GetBytes(16);
        var p = k.ExportParameters(true);
        save([.. id, .. p.D!, .. p.Q.X!, .. p.Q.Y!]);
        return new ShareIdentity(id, k, name);
    }

    public static ShareIdentity Ephemeral(string name) =>
        new(RandomNumberGenerator.GetBytes(16), ECDsa.Create(ECCurve.NamedCurves.nistP256), name);
}

public sealed record Hello(byte[] DeviceId, byte[] IdentityKey, byte[] EphemeralKey, byte[] Nonce, string Name)
{
    public byte[] Encode()
    {
        var w = new WireWriter();
        w.Bytes(Bhds.HelloMagic); w.U8(Bhds.Version);
        w.Bytes(DeviceId); w.Bytes(IdentityKey); w.Bytes(EphemeralKey); w.Bytes(Nonce);
        var n = Encoding.UTF8.GetBytes(Name);
        if (n.Length > 64) n = n[..64];
        w.U8((byte)n.Length); w.Bytes(n);
        return w.ToArray();
    }

    public static Hello Decode(ReadOnlySpan<byte> d)
    {
        var r = new WireReader(d);
        if (!r.Bytes(4).SequenceEqual(Bhds.HelloMagic)) throw new WireException("not a BHDisplay peer");
        if (r.U8() != Bhds.Version) throw new WireException("unsupported protocol version");
        var id = r.Bytes(16).ToArray(); var ik = r.Bytes(65).ToArray(); var ek = r.Bytes(65).ToArray();
        var nonce = r.Bytes(32).ToArray();
        int n = r.U8();
        if (n > 64) throw new WireException("name too long");
        var name = Encoding.UTF8.GetString(r.Bytes(n));
        if (r.Remaining != 0) throw new WireException("trailing bytes in HELLO");
        P256.Import(ik); P256.Import(ek);   // both must be real curve points
        return new Hello(id, ik, ek, nonce, name);
    }
}

public sealed record HandshakeKeys(byte[] Send, byte[] Receive, string PairCode);

public static class Handshake
{
    public static byte[] Transcript(byte[] dialerHello, byte[] listenerHello) =>
        SHA256.HashData([.. "BHDS-v1"u8, .. dialerHello, .. listenerHello]);

    static byte[] AuthMessage(byte role, byte[] t) => [.. "BHDS-auth"u8, role, .. t];

    /// ECDSA-P256-SHA256, raw r || s (IEEE P1363, 64 bytes) — CryptoKit's rawRepresentation.
    public static byte[] Sign(ShareIdentity id, byte role, byte[] t) =>
        id.SigningKey.SignData(AuthMessage(role, t), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public static bool Verify(byte[] sig, byte[] identityKey, byte role, byte[] t)
    {
        if (sig.Length != 64) return false;
        try
        {
            using var k = ECDsa.Create(P256.Import(identityKey));
            return k.VerifyData(AuthMessage(role, t), sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception) { return false; }
    }

    public static HandshakeKeys DeriveKeys(ECDiffieHellman ephemeral, byte[] peerEphemeral, byte[] t, bool isDialer)
    {
        using var peer = ECDiffieHellman.Create(P256.Import(peerEphemeral));
        var z = ephemeral.DeriveRawSecretAgreement(peer.PublicKey);    // x-coordinate, as CryptoKit's SharedSecret
        var k = HKDF.DeriveKey(HashAlgorithmName.SHA256, z, 64, t, "BHDS-v1 keys"u8.ToArray());
        var p = HKDF.DeriveKey(HashAlgorithmName.SHA256, z, 4, t, "BHDS-v1 pair"u8.ToArray());
        CryptographicOperations.ZeroMemory(z);
        var code = BinaryPrimitives.ReadUInt32BigEndian(p) % 1_000_000;
        var d2l = k[..32]; var l2d = k[32..];
        return new HandshakeKeys(isDialer ? d2l : l2d, isDialer ? l2d : d2l, code.ToString("D6"));
    }
}

/// AES-256-GCM per direction; nonce = 4 zero bytes || 64-bit big-endian sequence number.
public sealed class RecordCipher(byte[] key) : IDisposable
{
    private readonly AesGcm _aes = new(key, 16);
    private ulong _seq;

    private byte[] NextNonce()
    {
        var n = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(n.AsSpan(4), _seq++);
        return n;
    }
    public byte[] Seal(byte[] plain)
    {
        var o = new byte[plain.Length + 16];
        _aes.Encrypt(NextNonce(), plain, o.AsSpan(0, plain.Length), o.AsSpan(plain.Length));
        return o;
    }
    public byte[] Open(byte[] body)
    {
        if (body.Length < 16) throw new WireException("truncated message");
        var plain = new byte[body.Length - 16];
        _aes.Decrypt(NextNonce(), body.AsSpan(0, plain.Length), body.AsSpan(plain.Length), plain);
        return plain;
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
                w.Bytes(t.Length > Bhds.MaxClipboard ? t.AsSpan(0, Bhds.MaxClipboard) : t);
                break;
            case MonitorPorts p: w.U8(0x50); w.U8(p.Mac); w.U8(p.Other); break;
            case MonitorShows m: w.U8(0x51); w.U8(m.Code); break;
            case SwitchRequest q: w.U8(0x52); w.U8(q.Code); break;
            case Unknown u: w.U8(u.Type); break;
        }
        return w.ToArray();
    }

    public static ShareMsg Decode(ReadOnlySpan<byte> d)
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
                if (e > 4 || !float.IsFinite(p)) throw new WireException("bad edge");   // 4 = take over, no edge
                p = Math.Clamp(p, 0f, 1f);
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
            default: return new Unknown(t);
        }
    }
}
