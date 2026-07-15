using System;
using System.Security.Cryptography;

namespace RealmChat
{
    // Pinned release-signing public key. CI signs SHA256SUMS with the matching
    // private key (SIGNING_KEY repo secret); a release whose signature does
    // not verify against THIS key is refused outright, so a compromised repo
    // (or its Actions token) cannot feed installed clients a tampered build.
    //
    // The same key is committed as src/RealmChat/release-key.pub, and the
    // release workflow verifies each SHA256SUMS.sig against that file before
    // publishing - so a SIGNING_KEY secret that no longer matches this pin
    // fails the release, not the fielded clients.
    //
    // Rotation MUST be two-phase: ship an exe that trusts the new key first,
    // wait for the host PC to self-update, only then start signing with it
    // (fielded exes verify with the old pin until they self-update, and the
    // self-update download is itself gated by that verification).
    public static class ReleaseKey
    {
        // RSA-3072 modulus (big-endian), public exponent 65537.
        private const string ModulusB64 =
            "yX55Bu21yaj7w1qoRRa9CAUBdQtCGYvwbJdTP6tNsBonr78LA49UvgAiTamqRKZesrnpmRvGsX9URiSF6qaI" +
            "Q5q+zZwd9iPRu6trssspgKdjlBfIzeFkFfLTm2fY9DLK5CylJvJn7/4w9dtqYYY536aosB/4WFNa5vzhX0jo" +
            "LyehnTq1V4I9F6B/lDZZuHWsgaJeeNhforlygEupcRNpUA4sA/fCU5WOh1Cetd1ZovTQoxe99KqxkwzVTEAK" +
            "ipmiDjtlwpfqu9kO8D7bjhy/90bUydMETx6C73vF8JMIzEkFkCwRe6+9zZZAt60yDUWNyOqLh9Z45JvvUqqg" +
            "DB8XIOujmxGIC05jahDKjUx3Aq7cG5T1rkS4jIJxMbw89OVS5OGQh/yDMabbY8BG2q1npx7itiBsZeByHxFd" +
            "wOqd/u91XTUVuW8EtlcznqOB+Noma66fF2GCSWFNGD15u96F2E/8SuEb+Ln1TGMyTtUYoAxZlBhAWd1SEXpa" +
            "n7MUm2aL";

        // True iff sig is a valid RSA PKCS#1 v1.5 SHA-256 signature over data
        // (what `openssl dgst -sha256 -sign` produces).
        public static bool Verify(byte[] data, byte[] sig)
        {
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.ImportParameters(new RSAParameters
                {
                    Modulus = Convert.FromBase64String(ModulusB64),
                    Exponent = new byte[] { 1, 0, 1 },
                });
                try
                {
                    return rsa.VerifyData(data, sig, HashAlgorithmName.SHA256,
                                          RSASignaturePadding.Pkcs1);
                }
                catch (CryptographicException)
                {
                    return false;   // malformed signature blob = not valid
                }
            }
        }
    }
}
