using System.Numerics;
using System.Security.Cryptography;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// CPython's Mersenne Twister: the 624-word state, the array seeding Python's own
/// `seed` performs, and the tempered draws the `random` module is built on.
/// </summary>
internal sealed class PythonRandomGenerator
{
    private const int WordCount = 624;
    private const int MiddleWord = 397;
    private const uint MatrixA = 0x9908b0df;
    private const uint UpperMask = 0x80000000;
    private const uint LowerMask = 0x7fffffff;

    /// <summary>The words Python's `seed(None)` draws from the system, one state's worth.</summary>
    private const int SystemWords = WordCount;

    private readonly uint[] _state = new uint[WordCount];
    private readonly uint[] _system = new uint[SystemWords];
    private int _index = WordCount;

    /// <summary>The 624 words that carry the state.</summary>
    internal IReadOnlyList<uint> Words => _state;

    /// <summary>How far into the state the next draw reads.</summary>
    internal int Index => _index;

    /// <summary>`seed()`, which draws the state from the system's own randomness.</summary>
    internal void SeedFromSystem()
    {
        var bytes = new byte[SystemWords * 4];
        RandomNumberGenerator.Fill(bytes);
        for (var word = 0; word < SystemWords; word++)
        {
            _system[word] = BitConverter.ToUInt32(bytes, word * 4);
        }
        InitByArray(_system);
    }

    /// <summary>
    /// `seed(a)` for a number: Python takes the value apart into 32-bit words, least
    /// significant first, and mixes them in. Zero is one word of zero.
    /// </summary>
    internal void SeedNumber(BigInteger value)
    {
        if (value.Sign < 0)
        {
            value = BigInteger.Abs(value);
        }
        var bitCount = value.GetBitLength();
        var wordCount = bitCount == 0 ? 1 : (int)((bitCount - 1) / 32 + 1);
        var words = new uint[wordCount];
        var remaining = value;
        var mask = new BigInteger(uint.MaxValue);
        for (var word = 0; word < wordCount; word++)
        {
            words[word] = (uint)(remaining & mask);
            remaining >>= 32;
        }
        InitByArray(words);
    }

    /// <summary>`init_by_array`: the key's words are mixed into a freshly initialized state.</summary>
    private void InitByArray(ReadOnlySpan<uint> key)
    {
        InitGenrand(19650218);
        int i = 1,
            j = 0;
        var k = Math.Max(WordCount, key.Length);
        for (; k > 0; k--)
        {
            // The XOR is applied before the additions, as C's precedence reads it.
            _state[i] =
                (
                    (_state[i] ^ ((_state[i - 1] ^ (_state[i - 1] >> 30)) * 1664525))
                    + key[j]
                    + (uint)j
                ) & 0xffffffff;
            i++;
            j++;
            if (i >= WordCount)
            {
                _state[0] = _state[WordCount - 1];
                i = 1;
            }
            if (j >= key.Length)
            {
                j = 0;
            }
        }
        for (k = WordCount - 1; k > 0; k--)
        {
            _state[i] =
                ((_state[i] ^ ((_state[i - 1] ^ (_state[i - 1] >> 30)) * 1566083941)) - (uint)i)
                & 0xffffffff;
            i++;
            if (i >= WordCount)
            {
                _state[0] = _state[WordCount - 1];
                i = 1;
            }
        }
        _state[0] = 0x80000000;
        _index = WordCount;
    }

    /// <summary>`init_genrand`: the state a key is mixed into.</summary>
    private void InitGenrand(uint seed)
    {
        _state[0] = seed & 0xffffffff;
        for (var word = 1; word < WordCount; word++)
        {
            _state[word] =
                (1812433253 * (_state[word - 1] ^ (_state[word - 1] >> 30)) + (uint)word)
                & 0xffffffff;
        }
        _index = WordCount;
    }

    /// <summary>One tempered draw, `genrand_uint32`.</summary>
    internal uint NextUInt32()
    {
        if (_index >= WordCount)
        {
            for (var word = 0; word < WordCount; word++)
            {
                var next = (word + 1) % WordCount;
                var y = (_state[word] & UpperMask) | (_state[next] & LowerMask);
                _state[word] =
                    (_state[(word + MiddleWord) % WordCount] ^ (y >> 1))
                    ^ ((y & 1) != 0 ? MatrixA : 0);
            }
            _index = 0;
        }
        var value = _state[_index++];
        value ^= value >> 11;
        value ^= (value << 7) & 0x9d2c5680;
        value ^= (value << 15) & 0xefc60000;
        value ^= value >> 18;
        return value;
    }

    /// <summary>`setstate`: the words and the index a `getstate` handed out.</summary>
    internal void Restore(IReadOnlyList<uint> words, int index)
    {
        for (var word = 0; word < WordCount; word++)
        {
            _state[word] = words[word];
        }
        _index = index;
    }
}
