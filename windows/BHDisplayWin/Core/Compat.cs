// Small building blocks that .NET Framework 4.8 (built into Windows 10/11, so the app needs no runtime of its
// own) does not have. The same code also compiles on modern .NET, so tests/interop runs it on macOS against
// the Mac implementation, and tests/gcm checks it against published test vectors and .NET's own AesGcm.
using System.Security.Cryptography;
using System.Text;

namespace BHDisplay.Core;

public static class Bytes
{
    private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

    public static byte[] Random(int n) { var b = new byte[n]; lock (Rng) Rng.GetBytes(b); return b; }

    public static byte[] Concat(params byte[][] parts)
    {
        var o = new byte[parts.Sum(p => p.Length)];
        int i = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, o, i, p.Length); i += p.Length; }
        return o;
    }

    public static byte[] Slice(byte[] b, int start, int length)
    {
        var o = new byte[length];
        Buffer.BlockCopy(b, start, o, 0, length);
        return o;
    }

    public static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    public static byte[] Sha256(params byte[][] parts)
    {
        using var h = SHA256.Create();
        return h.ComputeHash(Concat(parts));
    }

    /// Compares without leaking where the first difference is.
    public static bool FixedTimeEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        int d = 0;
        for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
        return d == 0;
    }

    public static bool Equal(byte[] a, byte[] b) => a.Length == b.Length && a.SequenceEqual(b);

    /// Byte-wise ordering (memcmp), as the protocol's "lower device id" rule uses.
    public static int Compare(byte[] a, byte[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return a.Length.CompareTo(b.Length);
    }

    public static void Zero(byte[] b) => Array.Clear(b, 0, b.Length);

    public static string Hex(byte[] b)
    {
        var sb = new StringBuilder(b.Length * 2);
        foreach (var x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }

    public static byte[]? FromHex(string s)
    {
        if (s.Length % 2 != 0) return null;
        var o = new byte[s.Length / 2];
        for (int i = 0; i < o.Length; i++)
        {
            int hi = HexVal(s[2 * i]), lo = HexVal(s[2 * i + 1]);
            if (hi < 0 || lo < 0) return null;
            o[i] = (byte)(hi << 4 | lo);
        }
        return o;
    }
    private static int HexVal(char c) => c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;

    // Big-endian integers.
    public static void PutU32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    public static uint GetU32(byte[] b, int o) => (uint)b[o] << 24 | (uint)b[o + 1] << 16 | (uint)b[o + 2] << 8 | b[o + 3];
    public static void PutU64(byte[] b, int o, ulong v) { PutU32(b, o, (uint)(v >> 32)); PutU32(b, o + 4, (uint)v); }
    public static ulong GetU64(byte[] b, int o) => (ulong)GetU32(b, o) << 32 | GetU32(b, o + 4);
}

public static class Num
{
    public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    public static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    public static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    /// Milliseconds on a monotonic clock (Environment.TickCount64 does not exist on .NET Framework).
    public static long NowMs => Clock.ElapsedMilliseconds;
}

/// HKDF-SHA256 (RFC 5869).
public static class Hkdf
{
    public static byte[] Extract(byte[] salt, byte[] ikm)
    {
        using var h = new HMACSHA256(salt);
        return h.ComputeHash(ikm);
    }

    public static byte[] Expand(byte[] prk, byte[] info, int length)
    {
        if (length > 255 * 32) throw new ArgumentOutOfRangeException(nameof(length));
        using var h = new HMACSHA256(prk);
        var o = new byte[length];
        var t = Array.Empty<byte>();
        int done = 0;
        for (byte i = 1; done < length; i++)
        {
            t = h.ComputeHash(Bytes.Concat(t, info, new[] { i }));
            int n = Math.Min(t.Length, length - done);
            Buffer.BlockCopy(t, 0, o, done, n);
            done += n;
        }
        return o;
    }
}

/// AES-256-GCM with a 12-byte nonce and 16-byte tag (NIST SP 800-38D), built on the AES block cipher alone:
/// .NET Framework has no AesGcm. Branch-free GHASH (no tables, no data-dependent branches).
public sealed class AesGcm256 : IDisposable
{
    private readonly ICryptoTransform _ecb;
    private readonly ulong _hHi, _hLo;           // hash subkey H = E(K, 0^128)

    public AesGcm256(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("AES-256 key must be 32 bytes");
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None; aes.Key = key;
        _ecb = aes.CreateEncryptor();
        var h = Block(new byte[16]);
        _hHi = Bytes.GetU64(h, 0); _hLo = Bytes.GetU64(h, 8);
    }

    private byte[] Block(byte[] input)
    {
        var o = new byte[16];
        _ecb.TransformBlock(input, 0, 16, o, 0);
        return o;
    }

    /// Returns ciphertext || tag.
    public byte[] Seal(byte[] nonce, byte[] plain)
    {
        if (nonce.Length != 12) throw new ArgumentException("nonce must be 12 bytes");
        var o = new byte[plain.Length + 16];
        Ctr(nonce, plain, 0, plain.Length, o);
        var tag = Tag(nonce, o, plain.Length);
        Buffer.BlockCopy(tag, 0, o, plain.Length, 16);
        return o;
    }

    /// `body` = ciphertext || tag. Null if the tag doesn't match (nothing is decrypted then).
    public byte[]? Open(byte[] nonce, byte[] body)
    {
        if (nonce.Length != 12 || body.Length < 16) return null;
        int n = body.Length - 16;
        var expected = Tag(nonce, body, n);
        if (!Bytes.FixedTimeEquals(expected, Bytes.Slice(body, n, 16))) return null;
        var plain = new byte[n];
        Ctr(nonce, body, 0, n, plain);
        return plain;
    }

    // Counter mode starting at inc32(J0) = nonce || 00000002.
    private void Ctr(byte[] nonce, byte[] src, int off, int len, byte[] dst)
    {
        var ctr = new byte[16];
        Buffer.BlockCopy(nonce, 0, ctr, 0, 12);
        uint c = 2;
        for (int i = 0; i < len; i += 16)
        {
            Bytes.PutU32(ctr, 12, c++);
            var ks = Block(ctr);
            int m = Math.Min(16, len - i);
            for (int j = 0; j < m; j++) dst[i + j] = (byte)(src[off + i + j] ^ ks[j]);
        }
    }

    // Tag = E(K, J0) XOR GHASH_H(C || pad || len(A)=0 || len(C)), J0 = nonce || 00000001. No additional data.
    private byte[] Tag(byte[] nonce, byte[] cipher, int len)
    {
        ulong yHi = 0, yLo = 0;
        var blk = new byte[16];
        for (int i = 0; i < len; i += 16)
        {
            Array.Clear(blk, 0, 16);
            Buffer.BlockCopy(cipher, i, blk, 0, Math.Min(16, len - i));
            yHi ^= Bytes.GetU64(blk, 0); yLo ^= Bytes.GetU64(blk, 8);
            Mul(ref yHi, ref yLo);
        }
        yLo ^= (ulong)len * 8;                 // len(A) = 0 in the high half
        Mul(ref yHi, ref yLo);
        var j0 = new byte[16];
        Buffer.BlockCopy(nonce, 0, j0, 0, 12);
        Bytes.PutU32(j0, 12, 1);
        var e = Block(j0);
        var t = new byte[16];
        Bytes.PutU64(t, 0, yHi ^ Bytes.GetU64(e, 0));
        Bytes.PutU64(t, 8, yLo ^ Bytes.GetU64(e, 8));
        return t;
    }

    // Y = Y · H in GF(2^128), GCM bit order (Algorithm 1 of SP 800-38D), constant time.
    private void Mul(ref ulong yHi, ref ulong yLo)
    {
        ulong zHi = 0, zLo = 0, vHi = _hHi, vLo = _hLo;
        for (int i = 0; i < 128; i++)
        {
            ulong bit = i < 64 ? (yHi >> (63 - i)) & 1 : (yLo >> (127 - i)) & 1;
            ulong mask = 0UL - bit;
            zHi ^= vHi & mask; zLo ^= vLo & mask;
            ulong lsb = 0UL - (vLo & 1);
            vLo = (vLo >> 1) | (vHi << 63);
            vHi = (vHi >> 1) ^ (0xE100000000000000UL & lsb);
        }
        yHi = zHi; yLo = zLo;
    }

    public void Dispose() => _ecb.Dispose();
}

