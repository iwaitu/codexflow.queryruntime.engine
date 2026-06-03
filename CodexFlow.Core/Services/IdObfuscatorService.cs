using System;
using System.Text;
using CodexFlow.Core.Abstractions;

namespace CodexFlow.Core.Services;

public class IdObfuscatorService : IIdObfuscatorService
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const int MinLength = 4;
    private const long Salt = 0x5DEECE66DL;

    public string Encode(long id)
    {
        if (id <= 0)
        {
            throw new ArgumentException("ID must be positive.");
        }

        // Reversible XOR shuffle
        long shuffled = id ^ Salt;

        string base62 = ToBase62(shuffled);

        // Ensure minimum length by padding with the first character of the alphabet
        if (base62.Length < MinLength)
        {
            return base62.PadLeft(MinLength, Alphabet[0]);
        }
        return base62;
    }

    public long Decode(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            throw new ArgumentException("Encoded string cannot be empty.");
        }

        // Remove padding
        // Since we pad with Alphabet[0] ('a'), we can't just TrimStart('a') 
        // if the actual encoded value could start with 'a'.
        // However, for this TDD demo, we'll keep it simple and assume the first char is padding
        // if the string length was forced to 4. 
        // Actually, FromBase62 handles leading 'a's (as 0) naturally.

        long shuffled = FromBase62(encoded);
        return shuffled ^ Salt;
    }

    private static string ToBase62(long value)
    {
        if (value == 0) return Alphabet[0].ToString();
        var sb = new StringBuilder();
        // Handle negative result from XOR (if id ^ Salt is negative)
        // Convert to ulong for bitwise safety
        ulong uValue = (ulong)value;
        while (uValue > 0)
        {
            sb.Insert(0, Alphabet[(int)(uValue % 62)]);
            uValue /= 62;
        }
        return sb.ToString();
    }

    private static long FromBase62(string value)
    {
        ulong result = 0;
        foreach (char c in value)
        {
            int index = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (index < 0) continue;
            result = result * 62 + (ulong)index;
        }
        return (long)result;
    }
}
