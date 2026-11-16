using System.Security.Cryptography;

namespace Latch.Core.Security;

/// <summary>App PIN storage: salted PBKDF2-SHA256, compared in constant time. The PIN itself is never stored.</summary>
public static class PinHasher
{
    public const int DefaultIterations = 600_000;
    public const int MinLength = 6;
    public const int MaxLength = 12;

    public static bool IsValidFormat(string pin) =>
        pin.Length is >= MinLength and <= MaxLength && pin.All(char.IsAsciiDigit);

    public static (string Hash, string Salt) Hash(string pin, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool Verify(string pin, string hash, string salt, int iterations)
    {
        byte[] expected, saltBytes;
        try
        {
            expected = Convert.FromBase64String(hash);
            saltBytes = Convert.FromBase64String(salt);
        }
        catch (FormatException)
        {
            return false;
        }
        var actual = Rfc2898DeriveBytes.Pbkdf2(pin, saltBytes, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
