using System.Security.Cryptography;
using System.Text;

namespace Descope.Maui.Internal;

internal static class Pkce
{
    public static (string Verifier, string Challenge) Generate()
    {
        var verifier = RandomString();
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public static string RandomString(int byteCount = 32) => Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));

    public static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
