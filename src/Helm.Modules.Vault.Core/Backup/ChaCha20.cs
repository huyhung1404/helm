using System.Buffers.Binary;
using System.Numerics;

namespace Helm.Modules.Vault.Backup;

/// <summary>
/// The ChaCha20 stream cipher (RFC 8439: 256-bit key, 96-bit nonce, 32-bit block counter). Only used for the KDBX
/// inner stream that hides protected values inside the already-encrypted file; .NET offers ChaCha20 only as an AEAD.
/// </summary>
internal sealed class ChaCha20
{
    private readonly uint[] _state = new uint[16];
    private readonly byte[] _block = new byte[64];
    private int _used = 64;

    public ChaCha20(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint counter = 0)
    {
        if (key.Length != 32 || nonce.Length != 12) throw new ArgumentException("ChaCha20 needs a 32-byte key and a 12-byte nonce.");
        _state[0] = 0x61707865;
        _state[1] = 0x3320646e;
        _state[2] = 0x79622d32;
        _state[3] = 0x6b206574;
        for (var i = 0; i < 8; i++) _state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);
        _state[12] = counter;
        for (var i = 0; i < 3; i++) _state[13 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[(i * 4)..]);
    }

    /// <summary>XORs the next bytes of the key stream into <paramref name="data"/> (encrypts and decrypts).</summary>
    public void Apply(Span<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (_used == 64) NextBlock();
            data[i] ^= _block[_used++];
        }
    }

    private void NextBlock()
    {
        Span<uint> x = stackalloc uint[16];
        _state.CopyTo(x);
        for (var round = 0; round < 10; round++)
        {
            Quarter(x, 0, 4, 8, 12);
            Quarter(x, 1, 5, 9, 13);
            Quarter(x, 2, 6, 10, 14);
            Quarter(x, 3, 7, 11, 15);
            Quarter(x, 0, 5, 10, 15);
            Quarter(x, 1, 6, 11, 12);
            Quarter(x, 2, 7, 8, 13);
            Quarter(x, 3, 4, 9, 14);
        }
        for (var i = 0; i < 16; i++) BinaryPrimitives.WriteUInt32LittleEndian(_block.AsSpan(i * 4), x[i] + _state[i]);
        _state[12]++;
        _used = 0;
    }

    private static void Quarter(Span<uint> x, int a, int b, int c, int d)
    {
        x[a] += x[b]; x[d] = BitOperations.RotateLeft(x[d] ^ x[a], 16);
        x[c] += x[d]; x[b] = BitOperations.RotateLeft(x[b] ^ x[c], 12);
        x[a] += x[b]; x[d] = BitOperations.RotateLeft(x[d] ^ x[a], 8);
        x[c] += x[d]; x[b] = BitOperations.RotateLeft(x[b] ^ x[c], 7);
    }
}
