using System.Security.Cryptography;
using Arma3Manager.Api.Contracts;

namespace Arma3Manager.Api.Security;

/// <summary>Creates and verifies PBKDF2-SHA256 panel credentials.</summary>
public static class PasswordHasher
{
    const int Iterations = 100_000;

    public static PanelAuth Create(string username, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return new(username, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool Verify(string password, string salt, string expectedHash)
    {
        var saltBytes = Convert.FromBase64String(salt);
        var expected = Convert.FromBase64String(expectedHash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, Iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>Signs authenticated session state with the configured application secret.</summary>
public static class SessionProof
{
    const string Purpose = "arma3-manager-authenticated-session-v1";

    public static string Create(string secret)
    {
        var signature = HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes(Purpose));
        return Convert.ToBase64String(signature);
    }

    public static bool Verify(string secret, string? proof)
    {
        if (string.IsNullOrWhiteSpace(proof)) return false;
        byte[] supplied;
        try { supplied = Convert.FromBase64String(proof); }
        catch (FormatException) { return false; }
        var expected = Convert.FromBase64String(Create(secret));
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}
