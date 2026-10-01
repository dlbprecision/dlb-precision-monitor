using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace DlbPrecision.Updater
{
    internal static class ChecksumFile
    {
        public const int MaximumBytes = 4096;
        private static readonly Regex Line = new Regex(@"^\s*([0-9A-Fa-f]{64})(?:\s+\*?(.+?))?\s*$", RegexOptions.CultureInvariant);

        public static bool TryParse(string text, string expectedFileName, out string sha256)
        {
            sha256 = "";
            string first = text.TrimStart('\uFEFF').Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            Match match = Line.Match(first);
            if (!match.Success) return false;
            if (match.Groups[2].Success && !string.Equals(match.Groups[2].Value.Trim(), expectedFileName, StringComparison.OrdinalIgnoreCase)) return false;
            sha256 = match.Groups[1].Value.ToLowerInvariant();
            return true;
        }
    }

    internal sealed class SignatureFacts
    {
        public string? SignerCommonName { get; set; }
        public string? SignerOrganization { get; set; }
        public List<string> ChainCommonNames { get; set; } = new List<string>();
        public bool CodeSigning { get; set; }
        public bool Timestamped { get; set; }
        public bool SingleSigner { get; set; }
        public bool SignatureValid { get; set; }
        public bool ChainTrusted { get; set; }
        public string? RootThumbprint { get; set; }
    }

    internal static class PublisherPolicy
    {
        public const string Publisher = "DLB Precision, LLC";
        // Artifact Signing issues DLB's short-lived certificates under this root (valid until 2045). The
        // root is pinned rather than the leaf, which changes every few days.
        public const string MicrosoftIdentityRoot = "Microsoft Identity Verification Root Certificate Authority 2020";
        public const string MicrosoftIdentityRootThumbprint = "F40042E2E5F7E8EF8189FED15519AECE42C3BFA2";

        public static string? Evaluate(SignatureFacts facts)
        {
            if (!facts.SingleSigner || !facts.SignatureValid)
                return "The download's signature is not a single valid signature, so it wasn't installed.";
            if (!string.Equals(facts.SignerCommonName, Publisher, StringComparison.Ordinal)
                || !string.Equals(facts.SignerOrganization, Publisher, StringComparison.Ordinal))
                return "The download is signed by another publisher (" + (facts.SignerCommonName ?? "unknown") + "), not " + Publisher + ".";
            // A name alone proves nothing: the chain must be one Windows trusts, ending at the pinned root.
            if (!facts.ChainTrusted || facts.ChainCommonNames.LastOrDefault() != MicrosoftIdentityRoot
                || !string.Equals(facts.RootThumbprint, MicrosoftIdentityRootThumbprint, StringComparison.OrdinalIgnoreCase))
                return "The DLB signature was not issued through Microsoft's identity-verified signing service.";
            if (!facts.CodeSigning) return "The signing certificate is not for code signing.";
            if (!facts.Timestamped) return "The signature has no timestamp.";
            return null;
        }
    }

    internal sealed class VerificationResult
    {
        private VerificationResult(bool ok, string reason, bool retryable)
        {
            Ok = ok;
            Reason = reason;
            Retryable = retryable;
        }

        public bool Ok { get; }
        public string Reason { get; }
        public bool Retryable { get; }

        internal static VerificationResult Accepted() => new VerificationResult(true, "", false);
        internal static VerificationResult Rejected(string reason, bool retryable = false) => new VerificationResult(false, reason, retryable);
    }

    internal static class PackageVerifier
    {
        private const string CodeSigningUsage = "1.3.6.1.5.5.7.3.3";
        private const string Rfc3161Timestamp = "1.3.6.1.4.1.311.3.3.1";
        private const string LegacyCounterSignature = "1.2.840.113549.1.9.6";
        // Revocation could not be checked (offline or Microsoft's service unreachable). Refused, but worth a retry.
        private static readonly int[] RevocationUnavailable = { unchecked((int)0x80092013), unchecked((int)0x80092012), unchecked((int)0x800B010E) };

        // The caller opens the file with write and delete blocked and keeps it open until setup has
        // started, so the bytes checked here are the bytes Windows runs.
        public static VerificationResult Verify(FileStream package, string path, string expectedSha256, string expectedProductVersion)
        {
            package.Position = 0;
            string actual;
            using (var sha = SHA256.Create())
                actual = BitConverter.ToString(sha.ComputeHash(package)).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                return VerificationResult.Rejected("The download doesn't match its checksum, so it wasn't installed.");

            int trust = NativeMethods.VerifyEmbeddedSignature(path, package.SafeFileHandle);
            if (RevocationUnavailable.Contains(trust))
                return VerificationResult.Rejected("Couldn't confirm with Microsoft that the signing certificate is still valid. Check your internet connection, then try again.", true);
            if (trust != 0) return VerificationResult.Rejected("Windows could not verify the download's signature (0x" + trust.ToString("X8") + "), so it wasn't installed.");

            SignatureFacts? facts = ReadSignatureFacts(package);
            if (facts == null) return VerificationResult.Rejected("The download's signature could not be read, so it wasn't installed.");
            string? refusal = PublisherPolicy.Evaluate(facts);
            if (refusal != null) return VerificationResult.Rejected(refusal);

            string product = FileVersionInfo.GetVersionInfo(path).ProductVersion?.Trim() ?? "";
            if (!string.Equals(product, expectedProductVersion, StringComparison.Ordinal))
                return VerificationResult.Rejected("The download is version " + (product.Length > 0 ? product : "unknown") + ", not " + expectedProductVersion + ".");
            return VerificationResult.Accepted();
        }

        private static SignatureFacts? ReadSignatureFacts(Stream package)
        {
            byte[]? pkcs7 = ReadAuthenticodeBlob(package);
            if (pkcs7 == null) return null;
            try
            {
                var cms = new SignedCms();
                cms.Decode(pkcs7);
                if (cms.SignerInfos.Count == 0) return null;
                SignerInfo signer = cms.SignerInfos[0];
                X509Certificate2? certificate = signer.Certificate;
                if (certificate == null) return null;
                var facts = new SignatureFacts
                {
                    // With exactly one signer whose signature checks out under this certificate, the certificate
                    // judged here is the one whose signature Windows verified against the file.
                    SingleSigner = cms.SignerInfos.Count == 1,
                    SignatureValid = SignatureChecks(signer),
                    SignerCommonName = certificate.GetNameInfo(X509NameType.SimpleName, false),
                    SignerOrganization = Organization(certificate),
                    CodeSigning = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                        .Any(usage => usage.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == CodeSigningUsage)),
                    Timestamped = signer.UnsignedAttributes.Cast<CryptographicAttributeObject>()
                        .Any(attribute => attribute.Oid.Value == Rfc3161Timestamp || attribute.Oid.Value == LegacyCounterSignature)
                };
                using (var chain = new X509Chain())
                {
                    // Windows already checked revocation, and the short-lived leaf is expected to be past its end
                    // date after a few days (the timestamp proves it was valid when signed). The chain must still
                    // build to a root this PC trusts.
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
                    chain.ChainPolicy.ExtraStore.AddRange(cms.Certificates);
                    facts.ChainTrusted = chain.Build(certificate);
                    foreach (X509ChainElement element in chain.ChainElements)
                        facts.ChainCommonNames.Add(element.Certificate.GetNameInfo(X509NameType.SimpleName, false));
                    if (chain.ChainElements.Count > 0) facts.RootThumbprint = chain.ChainElements[chain.ChainElements.Count - 1].Certificate.Thumbprint;
                }
                return facts;
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        private static bool SignatureChecks(SignerInfo signer)
        {
            try
            {
                signer.CheckSignature(true); // the signature only; certificate trust is judged by the chain
                return true;
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        private static string? Organization(X509Certificate2 certificate)
        {
            // One RDN per line keeps a quoted "DLB Precision, LLC" intact.
            foreach (string line in certificate.SubjectName.Decode(X500DistinguishedNameFlags.UseNewLines).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith("O=", StringComparison.Ordinal)) continue;
                string value = line.Substring(2).Trim();
                if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"') value = value.Substring(1, value.Length - 2).Replace("\"\"", "\"");
                return value;
            }
            return null;
        }

        // Reads the PKCS#7 block from the PE security directory (an Authenticode-signed .exe).
        private static byte[]? ReadAuthenticodeBlob(Stream stream)
        {
            const int SecurityDirectory = 4;
            const ushort Pe32 = 0x10B, Pe32Plus = 0x20B, PkcsSignedData = 2;
            var reader = new BinaryReader(stream, Encoding.ASCII, true);
            if (stream.Length < 0x40) return null;
            stream.Position = 0;
            if (reader.ReadUInt16() != 0x5A4D) return null;
            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset > stream.Length - 0x100) return null;
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) return null;
            stream.Position = peOffset + 24;
            ushort magic = reader.ReadUInt16();
            long directories = magic == Pe32Plus ? peOffset + 24 + 112 : magic == Pe32 ? peOffset + 24 + 96 : -1;
            if (directories < 0) return null;
            stream.Position = directories + SecurityDirectory * 8;
            uint offset = reader.ReadUInt32();
            uint size = reader.ReadUInt32();
            if (offset == 0 || size < 8 || size > 1024 * 1024 || offset + (long)size > stream.Length) return null;
            stream.Position = offset;
            uint length = reader.ReadUInt32();
            reader.ReadUInt16();
            ushort type = reader.ReadUInt16();
            if (type != PkcsSignedData || length < 8 || length > size) return null;
            return reader.ReadBytes((int)length - 8);
        }
    }
}
