// SPDX-License-Identifier: MIT
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace QuietThings.Playback;

// Mutual proof without sending the reusable LAN token to a local listener.
public static class WebBridgeAuthentication
{
    static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static string Mac(string key, string message)
    { using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key)); return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message))).ToLowerInvariant(); }
    static bool Equal(string a, string b)
    { if (a == null || b == null || a.Length != 64 || b.Length != 64) return false; return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b)); }
    public static string Request(string key, string method, string path, byte[] body, DateTimeOffset now)
    {
        string stamp = now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string identity = stamp + ":" + nonce;
        return "QuietThings-HMAC " + identity + ":" + Mac(key, "qt-web-request-v1\n" + identity + "\n" + method + "\n" + path + "\n" + Digest(body));
    }
    public static bool VerifyRequest(string auth, string key, string method, string path, byte[] body, DateTimeOffset now, out string nonce)
    {
        nonce = null;
        if (string.IsNullOrEmpty(key) || key.Length < 16 || key.Length > 256 || auth == null || !auth.StartsWith("QuietThings-HMAC ", StringComparison.Ordinal)) return false;
        var parts = auth.Substring(17).Split(':');
        if (parts.Length != 3 || parts[1].Length != 32 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var stamp) || stamp < now.ToUnixTimeMilliseconds() - 15000 || stamp > now.ToUnixTimeMilliseconds() + 15000) return false;
        foreach (char c in parts[1]) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
        if (!Equal(parts[2], Mac(key, "qt-web-request-v1\n" + parts[0] + ":" + parts[1] + "\n" + method + "\n" + path + "\n" + Digest(body)))) return false;
        nonce = parts[1]; return true;
    }
    public static string Response(string key, string auth, int status, byte[] body) => Mac(key, "qt-web-response-v1\n" + auth + "\n" + status.ToString(CultureInfo.InvariantCulture) + "\n" + Digest(body));
    public static bool VerifyResponse(string signature, string key, string auth, int status, byte[] body) => Equal(signature, Response(key, auth, status, body));
}
