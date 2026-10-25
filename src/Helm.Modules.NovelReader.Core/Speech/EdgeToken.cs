using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Helm.Modules.NovelReader.Speech;

/// <summary>
/// The Sec-MS-GEC token (version 2, required since October 2026: version 1 is refused with 403) the read-aloud service
/// of Microsoft Edge checks: "&lt;CS64 as 16 hex&gt;-&lt;SHA-256 as 64 hex&gt;" of the Windows file time rounded down
/// to 5 minutes and the client token. The SHA-256 hashes a secret 57-byte prefix first, known here only as the state
/// it leaves; the CS64 is keyed by that digest. Ported from edge-tts (src/edge_tts/drm.py, MIT), where the constants
/// come from.
/// </summary>
internal static class EdgeToken
{
    private static readonly uint[] Initial = [0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19];

    /// <summary>The state after the first 14 rounds of the secret prefix's block.</summary>
    private static readonly uint[] PrefixState = [0xFD9FBEA2, 0x91243CE7, 0xBE499094, 0xCD2E1E0A, 0x6C006F49, 0x04BD35AB, 0xFEB0A7E4, 0x03EFFBD2];

    /// <summary>The prefix's share of words 16–29 of that block's schedule.</summary>
    private static readonly uint[] PrefixSchedule =
        [0xEB24BF9E, 0x1558691F, 0xA0C09EE2, 0x0CFA32BA, 0xA622EE8C, 0xD1DD9491, 0xC0DD25A0, 0xF9E70AB6, 0xF0C31573, 0x7058CE4E, 0x7188E606, 0x00E5AB33, 0xD258B060, 0x72766564];

    private static readonly uint[] K =
    [
        0x428A2F98, 0x71374491, 0xB5C0FBCF, 0xE9B5DBA5, 0x3956C25B, 0x59F111F1, 0x923F82A4, 0xAB1C5ED5,
        0xD807AA98, 0x12835B01, 0x243185BE, 0x550C7DC3, 0x72BE5D74, 0x80DEB1FE, 0x9BDC06A7, 0xC19BF174,
        0xE49B69C1, 0xEFBE4786, 0x0FC19DC6, 0x240CA1CC, 0x2DE92C6F, 0x4A7484AA, 0x5CB0A9DC, 0x76F988DA,
        0x983E5152, 0xA831C66D, 0xB00327C8, 0xBF597FC7, 0xC6E00BF3, 0xD5A79147, 0x06CA6351, 0x14292967,
        0x27B70A85, 0x2E1B2138, 0x4D2C6DFC, 0x53380D13, 0x650A7354, 0x766A0ABB, 0x81C2C92E, 0x92722C85,
        0xA2BFE8A1, 0xA81A664B, 0xC24B8B70, 0xC76C51A3, 0xD192E819, 0xD6990624, 0xF40E3585, 0x106AA070,
        0x19A4C116, 0x1E376C08, 0x2748774C, 0x34B0BCB5, 0x391C0CB3, 0x4ED8AA4A, 0x5B9CCA4F, 0x682E6FF3,
        0x748F82EE, 0x78A5636F, 0x84C87814, 0x8CC70208, 0x90BEFFFA, 0xA4506CEB, 0xBEF9A3F7, 0xC67178F2,
    ];

    private const uint Adjustment = 0xC9DC06A7;
    private const uint Sigma0Adjustment = 0x059C0B80;
    private const uint Sigma1Adjustment = 0x000B92C0;

    /// <summary>The first block's round constants: <see cref="K"/> with round 14 replaced.</summary>
    private static readonly uint[] PrefixK = [.. K[..14], Adjustment, .. K[15..]];

    private static readonly (uint Coefficient, uint Offset)[] Cs64Coefficients =
    [
        (0xE82E91B1, 0xB32700D0), (0x44DE24C6, 0x88DFF51D), (0x3BEB65BC, 0x29514038), (0xC89C5933, 0xB328CBDF),
        (0x8D1EC211, 0xD405B4D5), (0x314EF5D5, 0x26E55F3C), (0xE92491B1, 0x607801C6), (0xF0A5B9D5, 0x82B2C1FB),
        (0x06064CF3, 0xFA51A3DA), (0xDA79FA60, 0x207CC464), (0xBED20560, 0x3C3C74F3), (0x89F9C9F8, 0xD2D14B61),
        (0x35FA38C6, 0xBB04A2C1), (0xCB49E3C6, 0xC5626167),
    ];

    private const uint Cs64Factor = 0x82893933;
    private const uint Cs64Adjustment0 = 0xF2F90B1A;
    private const uint Cs64Adjustment1 = 0x0B1A0000;

    /// <summary>The token for a moment: the same for 5 minutes.</summary>
    public static string Generate(DateTimeOffset now, string clientToken)
    {
        var ticks = now.UtcDateTime.ToFileTimeUtc();
        ticks -= ticks % 3_000_000_000L;
        var data = Encoding.ASCII.GetBytes(ticks.ToString(CultureInfo.InvariantCulture) + clientToken);
        var digest = Sha256(data);
        var (low, high) = Cs64(data, digest);
        Span<byte> cs64 = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(cs64, low);
        BinaryPrimitives.WriteUInt32LittleEndian(cs64[4..], high);
        return Convert.ToHexString(cs64) + "-" + Convert.ToHexString(digest);
    }

    private static uint Sigma0(uint x) => uint.RotateRight(x, 7) ^ uint.RotateRight(x, 18) ^ (x >> 3);

    private static uint Sigma1(uint x) => uint.RotateRight(x, 17) ^ uint.RotateRight(x, 19) ^ (x >> 10);

    private static uint Rol16(uint x) => uint.RotateLeft(x, 16);

    private static void Expand(uint[] w, int from)
    {
        for (var i = from; i < 64; i++) w[i] = Sigma1(w[i - 2]) + w[i - 7] + Sigma0(w[i - 15]) + w[i - 16];
    }

    private static uint[] Rounds(uint[] state, uint[] w, uint[] k, int first)
    {
        uint a = state[0], b = state[1], c = state[2], d = state[3], e = state[4], f = state[5], g = state[6], h = state[7];
        for (var i = first; i < 64; i++)
        {
            var t1 = h + (uint.RotateRight(e, 6) ^ uint.RotateRight(e, 11) ^ uint.RotateRight(e, 25)) + (g ^ (e & (f ^ g))) + k[i] + w[i];
            var t2 = (uint.RotateRight(a, 2) ^ uint.RotateRight(a, 13) ^ uint.RotateRight(a, 22)) + ((a & b) | (c & (a | b)));
            (h, g, f, e, d, c, b, a) = (g, f, e, d + t1, c, b, a, t1 + t2);
        }
        return [a, b, c, d, e, f, g, h];
    }

    private static uint[] Add(uint[] x, uint[] y) => x.Zip(y, (p, q) => p + q).ToArray();

    /// <summary>The state after the secret prefix's block, which ends with the first 7 bytes of the data.</summary>
    private static uint[] PrefixBlock(ReadOnlySpan<byte> data)
    {
        var w = new uint[64];
        w[14] = (uint)(data[0] << 16 | data[1] << 8 | data[2]);
        w[15] = BinaryPrimitives.ReadUInt32BigEndian(data[3..7]);
        for (var i = 16; i < 30; i++)
        {
            var value = PrefixSchedule[i - 16];
            if (i - 2 >= 14) value += i == 16 ? Sigma1(w[i - 2]) ^ Sigma1Adjustment : Sigma1(w[i - 2]);
            if (i - 7 >= 14) value += w[i - 7];
            if (i - 15 >= 14) value += i == 29 ? Sigma0(w[i - 15]) ^ Sigma0Adjustment : Sigma0(w[i - 15]);
            w[i] = value;
        }
        w[30] = Sigma1(w[28]) + w[23] + Sigma0(w[15]) + w[14] + Adjustment - K[14];
        Expand(w, 31);
        return Add(Initial, Rounds(PrefixState, w, PrefixK, 14));
    }

    private static byte[] Sha256(byte[] data)
    {
        var length = 57 + data.Length;
        var zeros = ((55 - length) % 64 + 64) % 64;
        var padded = new byte[data.Length - 7 + 1 + zeros + 8];
        data.AsSpan(7).CopyTo(padded);
        padded[data.Length - 7] = 0x80;
        BinaryPrimitives.WriteUInt64BigEndian(padded.AsSpan(padded.Length - 8), (ulong)length * 8);
        var state = PrefixBlock(data);
        for (var at = 0; at < padded.Length; at += 64)
        {
            var w = new uint[64];
            for (var i = 0; i < 16; i++) w[i] = BinaryPrimitives.ReadUInt32BigEndian(padded.AsSpan(at + 4 * i, 4));
            Expand(w, 16);
            state = Add(state, Rounds(state, w, K, 0));
        }
        var digest = new byte[32];
        for (var i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32BigEndian(digest.AsSpan(4 * i), state[i]);
        return digest;
    }

    private static (uint Low, uint High) Cs64(byte[] data, byte[] digest)
    {
        var keyA = BinaryPrimitives.ReadUInt32LittleEndian(digest) | 1;
        var keyB = BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(4)) | 1;
        uint[] factors = [keyA * Cs64Factor, keyB * Cs64Factor];
        var terms = Cs64Coefficients.Select((c, i) => c.Coefficient * factors[i % 2]).ToList();
        var offsets = Cs64Coefficients.Select(c => c.Offset).ToList();

        // A zero byte, then the data, as little-endian words (zero-padded) of an even count.
        var bytes = new byte[(data.Length + 1 + 3) / 4 * 4];
        data.CopyTo(bytes, 1);
        var words = Enumerable.Range(0, bytes.Length / 4).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4 * i))).ToList();
        if (words.Count % 2 == 1) words.Add(0);
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            var term = word * (i % 2 == 0 ? keyA : keyB);
            var offset = Rol16(word) * (i % 2 == 0 ? 0xEF0569FB : 0xC31713DB);
            if (i == 0)
            {
                term += Cs64Adjustment0 * factors[0];
                offset += Cs64Adjustment1;
            }
            terms.Add(term);
            offsets.Add(offset);
        }

        uint swapAcc = 0, swapLast = 0, state = 0, acc = 0;
        for (var i = 0; i < terms.Count; i += 2)
        {
            uint x = terms[i], y = terms[i + 1];
            var q = Rol16(offsets[i] + x) * 0x76F50F34;
            swapAcc += Rol16(q) * 0xC3EFEA97 + q;
            q = Rol16(offsets[i + 1] + y) * 0x1881CE3C;
            swapLast = Rol16(q) * 0x35BD1EC9 + q;
            swapAcc += swapLast;

            state = state * keyA + x;
            state = Rol16(state) * 0xCF98B111;
            state = Rol16(state) * 0x87085B9F;
            state = Rol16(state) * 0x12CEB96D;
            state = Rol16(state) * 0x257E1D83;
            acc += state;
            var t = state * keyB + y;
            t = Rol16(t) * 0xA27416F5;
            t = Rol16(t) * 0xD38396FF;
            state = Rol16(Rol16(t) * 0x7C932B89) * 0xBFA49F69;
            acc += state;
        }
        return (swapLast ^ state, swapAcc ^ acc);
    }
}
