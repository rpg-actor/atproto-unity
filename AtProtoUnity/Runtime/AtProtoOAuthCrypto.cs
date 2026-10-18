using System;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Newtonsoft.Json.Linq;

namespace AtProtoUnity
{
    public sealed class AtProtoPkceChallenge
    {
        public string Verifier { get; }
        public string Challenge { get; }

        public AtProtoPkceChallenge(string verifier, string challenge)
        {
            Verifier = verifier;
            Challenge = challenge;
        }
    }

    public sealed class AtProtoDpopKeyPair : IDisposable
    {
        internal AtProtoDpopSigner Signer { get; }
        public string PublicJwk { get; }

        internal AtProtoDpopKeyPair(
            AtProtoDpopSigner signer,
            string publicJwk)
        {
            Signer = signer;
            PublicJwk = publicJwk;
        }

        public void Dispose()
        {
            Signer?.Dispose();
        }
    }

    sealed class AtProtoDpopSigner : IDisposable
    {
        readonly ECPrivateKeyParameters privateKey;

        public AtProtoDpopSigner(ECPrivateKeyParameters privateKey)
        {
            this.privateKey = privateKey ?? throw new ArgumentNullException(nameof(privateKey));
        }

        public byte[] Sign(byte[] hash)
        {
            var signer = new ECDsaSigner(new HMacDsaKCalculator(new Org.BouncyCastle.Crypto.Digests.Sha256Digest()));
            signer.Init(true, privateKey);
            var signature = signer.GenerateSignature(hash);
            var result = new byte[64];
            CopyFixed(signature[0].ToByteArrayUnsigned(), result, 0);
            CopyFixed(signature[1].ToByteArrayUnsigned(), result, 32);
            return result;
        }

        public void Dispose()
        {
        }

        static void CopyFixed(byte[] source, byte[] destination, int destinationOffset)
        {
            if (source.Length > 32)
                throw new InvalidOperationException("The ECDSA signature integer was too large.");
            Buffer.BlockCopy(source, 0, destination, destinationOffset + 32 - source.Length, source.Length);
        }
    }

    static class AtProtoOAuthCrypto
    {
        public static string CreateRandomToken(int byteCount)
        {
            var bytes = new byte[byteCount];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(bytes);
            return ToBase64Url(bytes);
        }

        public static AtProtoPkceChallenge CreatePkceChallenge()
        {
            var verifier = CreateRandomToken(32);
            byte[] digest;
            using (var sha256 = SHA256.Create())
                digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(verifier));
            return new AtProtoPkceChallenge(verifier, ToBase64Url(digest));
        }

        public static AtProtoDpopKeyPair CreateDpopKeyPair()
        {
            var domain = CreateDomainParameters();
            var generator = new ECKeyPairGenerator();
            generator.Init(new ECKeyGenerationParameters(domain, new SecureRandom()));
            var keyPair = generator.GenerateKeyPair();
            return CreateKeyPair(
                (ECPrivateKeyParameters)keyPair.Private,
                (ECPublicKeyParameters)keyPair.Public);
        }

        public static string CreateDpopProof(
            AtProtoDpopKeyPair keyPair,
            string method,
            string url,
            string nonce,
            string accessToken = null)
        {
            if (keyPair == null || keyPair.Signer == null)
                throw new InvalidOperationException("A DPoP key pair is required.");

            var header = new JObject
            {
                ["typ"] = "dpop+jwt",
                ["alg"] = "ES256",
                ["jwk"] = JObject.Parse(keyPair.PublicJwk)
            };
            var payload = new JObject
            {
                ["jti"] = CreateRandomToken(16),
                ["htm"] = method.ToUpperInvariant(),
                ["htu"] = RemoveQuery(url),
                ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            if (!string.IsNullOrEmpty(nonce))
                payload["nonce"] = nonce;
            if (!string.IsNullOrEmpty(accessToken))
            {
                byte[] digest;
                using (var sha256 = SHA256.Create())
                    digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(accessToken));
                payload["ath"] = ToBase64Url(digest);
            }

            var encodedHeader = ToBase64Url(Encoding.UTF8.GetBytes(header.ToString(Newtonsoft.Json.Formatting.None)));
            var encodedPayload = ToBase64Url(Encoding.UTF8.GetBytes(payload.ToString(Newtonsoft.Json.Formatting.None)));
            var unsignedToken = encodedHeader + "." + encodedPayload;
            var proofDigest = new Org.BouncyCastle.Crypto.Digests.Sha256Digest();
            var unsignedBytes = Encoding.UTF8.GetBytes(unsignedToken);
            proofDigest.BlockUpdate(unsignedBytes, 0, unsignedBytes.Length);
            var hash = new byte[proofDigest.GetDigestSize()];
            proofDigest.DoFinal(hash, 0);
            return unsignedToken + "." + ToBase64Url(keyPair.Signer.Sign(hash));
        }

        static AtProtoDpopKeyPair CreateKeyPair(
            ECPrivateKeyParameters privateKey,
            ECPublicKeyParameters publicKey)
        {
            var publicPoint = publicKey.Q.Normalize().GetEncoded(false);
            var x = CopyFixed(publicPoint, 1, 32);
            var y = CopyFixed(publicPoint, 33, 32);
            var publicJwk = new JObject
            {
                ["kty"] = "EC",
                ["crv"] = "P-256",
                ["x"] = ToBase64Url(x),
                ["y"] = ToBase64Url(y)
            };
            return new AtProtoDpopKeyPair(
                new AtProtoDpopSigner(privateKey),
                publicJwk.ToString(Newtonsoft.Json.Formatting.None));
        }

        static ECDomainParameters CreateDomainParameters()
        {
            var curve = ECNamedCurveTable.GetByName("P-256");
            if (curve == null)
                throw new InvalidOperationException("The P-256 curve is not available.");
            return new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H, curve.GetSeed());
        }

        static byte[] CopyFixed(byte[] source, int sourceOffset, int length)
        {
            if (source == null || sourceOffset < 0 || length < 1 || source.Length - sourceOffset < length)
                throw new InvalidOperationException("The P-256 key coordinate was invalid.");
            var result = new byte[length];
            Buffer.BlockCopy(source, sourceOffset, result, 0, length);
            return result;
        }

        static string RemoveQuery(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                throw new ArgumentException("A valid absolute URL is required.", nameof(url));
            return uri.GetLeftPart(UriPartial.Path);
        }

        static string ToBase64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
    }
}