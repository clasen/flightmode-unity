using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace FlightmodeSdk
{
    // Public RSA flight key as published by get_sdk_config (JWK modulus and exponent, base64url).
    public sealed class FlightKey
    {
        public string Kid;
        public string N;
        public string E;

        // Parses the flightKeys JSON array returned by get_sdk_config.
        public static List<FlightKey> ParseList(string json)
        {
            var node = Json.Parse(json);
            if (node.Type != JsonNode.Kind.Array) throw new FormatException("flight keys must be a JSON array");
            var keys = new List<FlightKey>();
            foreach (var item in node.ArrayValue) keys.Add(new FlightKey { Kid = Read.Str(item, "kid"), N = Read.Str(item, "n"), E = Read.Str(item, "e") });
            return keys;
        }
    }

    public sealed class SignatureException : Exception
    {
        public SignatureException(string message) : base(message) { }
    }

    public static class Crypto
    {
        public static byte[] Base64Url(string value)
        {
            var text = value.Replace('-', '+').Replace('_', '/');
            switch (text.Length % 4)
            {
                case 2: text += "=="; break;
                case 3: text += "="; break;
            }
            return Convert.FromBase64String(text);
        }

        // Verifies an RSA PKCS#1 v1.5 SHA-256 flight envelope and returns its payload bytes.
        public static byte[] VerifyFlightEnvelope(JsonNode envelope, IReadOnlyList<FlightKey> keys)
        {
            var keyId = Read.Str(envelope, "keyId");
            var payload = Convert.FromBase64String(Read.Str(envelope, "payload"));
            var signature = Convert.FromBase64String(Read.Str(envelope, "sig"));
            FlightKey key = null;
            foreach (var candidate in keys) if (candidate.Kid == keyId) key = candidate;
            if (key == null) throw new SignatureException("unknown flight key " + keyId);
            using (var rsa = RSA.Create())
            {
                rsa.ImportParameters(new RSAParameters { Modulus = Base64Url(key.N), Exponent = Base64Url(key.E) });
                if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                {
                    throw new SignatureException("flight signature mismatch");
                }
            }
            return payload;
        }

        public static string HmacBase64(string installKeyBase64, byte[] payload)
        {
            using (var hmac = new HMACSHA256(Convert.FromBase64String(installKeyBase64)))
            {
                return Convert.ToBase64String(hmac.ComputeHash(payload));
            }
        }

        public static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) builder.Append(b.ToString("x2"));
                return builder.ToString();
            }
        }

        public static string NewInstallId()
        {
            var bytes = new byte[16];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            var builder = new StringBuilder("i");
            foreach (var b in bytes) builder.Append(b.ToString("x2"));
            return builder.ToString();
        }
    }
}
