using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace DlbPrecision.Updater
{
    internal enum OfferStatus { UpToDate, Available, NotAvailable }

    internal sealed class UpdateDecision
    {
        private UpdateDecision(OfferStatus status, UpdateOffer? offer)
        {
            Status = status;
            Offer = offer;
        }

        public OfferStatus Status { get; }
        public UpdateOffer? Offer { get; }

        internal static UpdateDecision UpToDate() => new UpdateDecision(OfferStatus.UpToDate, null);
        internal static UpdateDecision NotAvailable() => new UpdateDecision(OfferStatus.NotAvailable, null);
        internal static UpdateDecision Available(UpdateOffer offer) => new UpdateDecision(OfferStatus.Available, offer);
    }

    internal sealed class UpdateOffer
    {
        // [0-9], not \d: \d also matches other scripts' digits, which int.Parse rejects.
        private static readonly Regex TagPattern = new Regex(@"^v([0-9]{1,4})\.([0-9]{1,4})\.([0-9]{1,5})$", RegexOptions.CultureInvariant);

        private UpdateOffer(Version version, string title, string notes, ReleaseAsset installer, ReleaseAsset checksum)
        {
            Version = version;
            VersionText = version.ToString(3);
            Title = title;
            Notes = notes;
            Installer = installer;
            Checksum = checksum;
        }

        public Version Version { get; }
        public string VersionText { get; }
        public string Title { get; }
        public string Notes { get; }
        public ReleaseAsset Installer { get; }
        public ReleaseAsset Checksum { get; }

        public static string InstallerName(string versionText) => "DLB-Precision-Monitor-" + versionText + "-Setup.exe";

        public static bool TryParseTag(string? tag, out Version version)
        {
            version = new Version(0, 0, 0);
            Match match = TagPattern.Match(tag ?? "");
            if (!match.Success) return false;
            version = new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
            return true;
        }

        // Compared as four parts, so a local test build such as 0.1.7.9 sorts below the release 0.1.8.
        public static Version Normalize(Version version) =>
            new Version(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build), Math.Max(0, version.Revision));

        // requiredDownloadPrefix is set for the real feed so files can only come from DLB's own GitHub releases.
        public static UpdateDecision Decide(ReleaseInfo? release, Version installed, bool allowPrerelease, bool allowFileUrls,
            string? requiredDownloadPrefix = null)
        {
            if (release == null || release.Draft || (release.Prerelease && !allowPrerelease)) return UpdateDecision.UpToDate();
            if (!TryParseTag(release.Tag, out Version version)) return UpdateDecision.NotAvailable();
            if (Normalize(version) <= Normalize(installed)) return UpdateDecision.UpToDate();

            string versionText = version.ToString(3);
            ReleaseAsset? installer = release.Assets.FirstOrDefault(asset => asset.Name == InstallerName(versionText));
            ReleaseAsset? checksum = release.Assets.FirstOrDefault(asset => asset.Name == InstallerName(versionText) + ".sha256");
            if (installer == null || checksum == null) return UpdateDecision.NotAvailable();
            if (installer.Size <= 0 || installer.Size > Downloader.MaximumBytes || checksum.Size <= 0 || checksum.Size > ChecksumFile.MaximumBytes)
                return UpdateDecision.NotAvailable();
            if (!AllowedDownload(installer.DownloadUrl, allowFileUrls) || !AllowedDownload(checksum.DownloadUrl, allowFileUrls))
                return UpdateDecision.NotAvailable();
            if (requiredDownloadPrefix != null && (!installer.DownloadUrl.StartsWith(requiredDownloadPrefix, StringComparison.Ordinal)
                || !checksum.DownloadUrl.StartsWith(requiredDownloadPrefix, StringComparison.Ordinal)))
                return UpdateDecision.NotAvailable();

            string title = release.Name.Length > 0 ? release.Name : "DLB Precision Monitor " + versionText;
            return UpdateDecision.Available(new UpdateOffer(version, title, PlainText.FromMarkdown(release.Body), installer, checksum));
        }

        private static bool AllowedDownload(string url, bool allowFileUrls) =>
            Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || (allowFileUrls && uri.IsFile && !uri.IsUnc));
    }
}
