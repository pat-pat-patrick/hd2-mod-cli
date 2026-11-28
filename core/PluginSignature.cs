using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Hd2Mod
{
    /// <summary>
    /// Who built a plugin file (the user, 2026-11-28: an official plugin built after the official
    /// list was written, or a version never listed, was shown as a third-party one). The
    /// maintainer signs every official build with a key only the maintainer holds; this program
    /// knows its public half. A signed file is an official plugin whatever its version, and the
    /// assembly's name says which one. The SHA-256 of the official list's releases still counts
    /// for the releases already out.
    ///
    /// The signature sits at the file's end, after the assembly, where the loader does not look:
    ///
    ///     [the assembly][signature][key id: 8 bytes][signature's length: u32 LE]["HD2QDSG1"]
    ///
    /// The signature is RSA PKCS#1 v1.5 over SHA-256 of Domain then the assembly's bytes. The key
    /// id is the first 8 bytes of SHA-256 of the key's modulus (big-endian, no leading zero).
    /// hd2-mod-cli\tools\sign_plugin.py writes it.
    /// </summary>
    public static class PluginSignature
    {
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("HD2QDSG1");
        static readonly byte[] Domain = Encoding.ASCII.GetBytes("HD2QD plugin signature v1\n");

        /// <summary>A key whose signature makes a plugin official: its modulus and exponent.</summary>
        internal sealed class Key
        {
            public string Owner;        // "official"
            public byte[] Modulus, Exponent;
            public string Id => IdOf(Modulus);
        }

        /// <summary>The maintainer's key (tools\sign_plugin.py --public prints it).</summary>
        const string OfficialModulus = "2eLwWdAwyHvSsH3VaF2t1cA1BSUoh6eaQQjWNVZrzXeCjtCr4gigwy3VJeEMtrNqMHt3f+KVc6HfUgQl7i+9+9apILBg2yGyllCDONLnKxX/pHfjsnDd7ke5nRkFZl5DjzF8eJvP8bHtpNO5YttcmVXHIz6/BlPaeoc0y/b9Pm6u1wRjPqbXo49TopJFxrWw2rGIrWGmZRLBGVh6SOZUED5/MJa9v5M6r+puYvkV0yZtyL2zJ0K2UZz119CsDG9YupC1hNapWwrPNfNBUtO5cKZcIvh8eUdC9jYDiYLbT9JJLSvFTSsCcPcSl5VP/rQxMkXJulLM9KepyZ3WAi8vS63C4D2eRygDSgpUY1AFG3pn9FFM1ukEhnRykNRFIkcL3yKN1Jo/uWfQL6USuPgetBdD3UInnZjdYDuQHKFfVNpcxhvBks66u4QhdNa1MgxIBVce8fdIdpPB3Oqnj4iq7jYQmBApj3dOmJkATeevndUnhZLsGKbiZ9Lvbu9oCucx";

        /// <summary>The keys trusted: the maintainer's; tests add one of their own.</summary>
        internal static readonly List<Key> Trusted = new List<Key>
        {
            new Key { Owner = "official", Modulus = Convert.FromBase64String(OfficialModulus), Exponent = new byte[] { 1, 0, 1 } },
        };

        /// <summary>The key id of a modulus: the first 8 bytes of its SHA-256, in hex.</summary>
        internal static string IdOf(byte[] modulus)
        {
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(modulus), 0, 8).Replace("-", "").ToLowerInvariant();
        }

        static readonly Dictionary<string, (long Length, DateTime Time, string Owner)> read =
            new Dictionary<string, (long, DateTime, string)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Who signed the plugin file at path: "official" when the maintainer's key did,
        /// else null (no signature, a key not trusted, or bytes changed after signing). Read once
        /// while the file is unchanged.</summary>
        public static string Signer(string path)
        {
            FileInfo fi;
            try { fi = new FileInfo(path); if (!fi.Exists) return null; }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { return null; }
            if (read.TryGetValue(fi.FullName, out var r) && r.Length == fi.Length && r.Time == fi.LastWriteTimeUtc) return r.Owner;
            string owner;
            try { owner = Verify(File.ReadAllBytes(fi.FullName)); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
            read[fi.FullName] = (fi.Length, fi.LastWriteTimeUtc, owner);
            return owner;
        }

        /// <summary>Whether the plugin file at path is signed by the maintainer.</summary>
        public static bool Official(string path) => Signer(path) == "official";

        /// <summary>The owner of the trusted key that signed these bytes, else null.</summary>
        internal static string Verify(byte[] file)
        {
            int n = file.Length;
            if (n < Magic.Length + 4 + 8 + 1) return null;
            for (int i = 0; i < Magic.Length; i++)
                if (file[n - Magic.Length + i] != Magic[i]) return null;
            int sigLen = BitConverter.ToInt32(file, n - Magic.Length - 4);
            int idAt = n - Magic.Length - 4 - 8;
            int sigAt = idAt - sigLen;
            if (sigLen < 128 || sigLen > 1024 || sigAt <= 0) return null;
            var id = BitConverter.ToString(file, idAt, 8).Replace("-", "").ToLowerInvariant();
            var key = Trusted.FirstOrDefault(k => k.Id == id);
            if (key == null || key.Modulus.Length != sigLen) return null;
            var sig = new byte[sigLen];
            Buffer.BlockCopy(file, sigAt, sig, 0, sigLen);
            byte[] hash;
            using (var h = SHA256.Create())
            {
                h.TransformBlock(Domain, 0, Domain.Length, null, 0);
                h.TransformFinalBlock(file, 0, sigAt);
                hash = h.Hash;
            }
            try
            {
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.ImportParameters(new RSAParameters { Modulus = key.Modulus, Exponent = key.Exponent });
                    return rsa.VerifyHash(hash, CryptoConfig.MapNameToOID("SHA256"), sig) ? key.Owner : null;
                }
            }
            catch (CryptographicException) { return null; }
        }

        /// <summary>The bytes signed: the file without a signature at its end (as it is when it
        /// has none). For the tests' own signing.</summary>
        internal static byte[] Unsigned(byte[] file)
        {
            int n = file.Length;
            if (n < Magic.Length + 12 || !Magic.Select((b, i) => file[n - Magic.Length + i] == b).All(x => x)) return file;
            int sigLen = BitConverter.ToInt32(file, n - Magic.Length - 4);
            int sigAt = n - Magic.Length - 4 - 8 - sigLen;
            if (sigLen <= 0 || sigAt <= 0) return file;
            var body = new byte[sigAt];
            Buffer.BlockCopy(file, 0, body, 0, sigAt);
            return body;
        }

        /// <summary>The file signed with the key (its private half): the format above. For tests;
        /// releases are signed by tools\sign_plugin.py.</summary>
        internal static byte[] Sign(byte[] file, RSA rsa)
        {
            var body = Unsigned(file);
            var p = rsa.ExportParameters(false);
            byte[] hash;
            using (var h = SHA256.Create())
            {
                h.TransformBlock(Domain, 0, Domain.Length, null, 0);
                h.TransformFinalBlock(body, 0, body.Length);
                hash = h.Hash;
            }
            var sig = rsa.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var id = IdOf(p.Modulus);
            var idBytes = Enumerable.Range(0, 8).Select(i => Convert.ToByte(id.Substring(i * 2, 2), 16)).ToArray();
            using (var m = new MemoryStream())
            {
                m.Write(body, 0, body.Length);
                m.Write(sig, 0, sig.Length);
                m.Write(idBytes, 0, 8);
                m.Write(BitConverter.GetBytes(sig.Length), 0, 4);
                m.Write(Magic, 0, Magic.Length);
                return m.ToArray();
            }
        }
    }
}
