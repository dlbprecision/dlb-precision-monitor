using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using DlbPrecision.Updater;

internal static class UpdaterTests
{
    private const string Repo = "https://github.com/dlbprecision/dlb-precision-monitor/releases/download/";

    public static void Run(Action<bool, string> check, bool integration)
    {
        Offers(check);
        Feeds(check);
        Checksums(check);
        Publishers(check);
        Outcomes(check);
        Notes(check);
        Downloads(check);
        Verification(check, integration);
    }

    private static ReleaseInfo Release(string tag, string? assetVersion = null, bool draft = false, bool prerelease = false,
        bool checksum = true, string scheme = "https", long size = 7_400_000)
    {
        string version = assetVersion ?? tag.TrimStart('v');
        string baseUrl = scheme == "https" ? Repo + tag + "/" : scheme + ":///C:/feed/";
        var release = new ReleaseInfo { Tag = tag, Name = "DLB Precision Monitor " + tag, Body = "Fixes", Draft = draft, Prerelease = prerelease };
        release.Assets.Add(new ReleaseAsset { Name = "DLB-Precision-Monitor-" + version + "-Setup.exe", Size = size,
            DownloadUrl = baseUrl + "DLB-Precision-Monitor-" + version + "-Setup.exe" });
        if (checksum)
            release.Assets.Add(new ReleaseAsset { Name = "DLB-Precision-Monitor-" + version + "-Setup.exe.sha256", Size = 104,
                DownloadUrl = baseUrl + "DLB-Precision-Monitor-" + version + "-Setup.exe.sha256" });
        return release;
    }

    private static void Offers(Action<bool, string> check)
    {
        var installed = new Version(0, 1, 8, 0);
        UpdateDecision newer = UpdateOffer.Decide(Release("v0.1.9"), installed, allowPrerelease: false, allowFileUrls: false);
        check(newer.Status == OfferStatus.Available && newer.Offer?.VersionText == "0.1.9"
            && newer.Offer.Installer.Name == "DLB-Precision-Monitor-0.1.9-Setup.exe" && newer.Offer.Checksum.Name.EndsWith(".sha256"),
            "A newer Latest release with both assets is offered.");
        check(UpdateOffer.Decide(Release("v0.1.8"), installed, false, false).Status == OfferStatus.UpToDate,
            "The installed version is reported as up to date.");
        check(UpdateOffer.Decide(Release("v0.1.7"), installed, false, false).Status == OfferStatus.UpToDate,
            "An older release is never offered as a downgrade.");
        check(UpdateOffer.Decide(null, installed, false, false).Status == OfferStatus.UpToDate,
            "No Latest release yet means up to date.");
        check(UpdateOffer.Decide(Release("v0.1.10"), new Version(0, 1, 9, 0), false, false).Status == OfferStatus.Available,
            "Versions compare numerically: 0.1.10 is newer than 0.1.9.");
        check(UpdateOffer.Decide(Release("v0.1.8"), new Version(0, 1, 7, 9), false, false).Status == OfferStatus.Available,
            "A local four-part test build is older than the next release.");
        check(UpdateOffer.Decide(Release("v0.1.9", draft: true), installed, true, false).Status == OfferStatus.UpToDate,
            "Draft releases are never offered.");
        check(UpdateOffer.Decide(Release("v0.1.9", prerelease: true), installed, false, false).Status == OfferStatus.UpToDate
            && UpdateOffer.Decide(Release("v0.1.9", prerelease: true), installed, true, false).Status == OfferStatus.Available,
            "Pre-releases are offered only through an explicit test feed.");
        check(UpdateOffer.Decide(Release("0.1.9"), installed, false, false).Status == OfferStatus.NotAvailable
            && UpdateOffer.Decide(Release("v0.1.9-beta"), installed, false, false).Status == OfferStatus.NotAvailable,
            "Tags that are not vMAJOR.MINOR.PATCH are refused.");
        check(UpdateOffer.Decide(Release("v0.1.9", checksum: false), installed, false, false).Status == OfferStatus.NotAvailable,
            "A release without its checksum file is refused.");
        check(UpdateOffer.Decide(Release("v0.1.9", assetVersion: "0.1.8"), installed, false, false).Status == OfferStatus.NotAvailable,
            "Asset names must match the release version.");
        check(UpdateOffer.Decide(Release("v0.1.9", scheme: "http"), installed, false, false).Status == OfferStatus.NotAvailable,
            "Unencrypted download addresses are refused.");
        check(UpdateOffer.Decide(Release("v0.1.9", scheme: "file"), installed, false, false).Status == OfferStatus.NotAvailable
            && UpdateOffer.Decide(Release("v0.1.9", scheme: "file"), installed, false, true).Status == OfferStatus.Available,
            "Local file downloads are allowed only for a local test feed.");
        check(UpdateOffer.Decide(Release("v0.1.9", size: 0), installed, false, false).Status == OfferStatus.NotAvailable
            && UpdateOffer.Decide(Release("v0.1.9", size: Downloader.MaximumBytes + 1), installed, false, false).Status == OfferStatus.NotAvailable,
            "Empty or oversized installers are refused before downloading.");
        ReleaseInfo elsewhere = Release("v0.1.9");
        foreach (ReleaseAsset asset in elsewhere.Assets) asset.DownloadUrl = asset.DownloadUrl.Replace("https://github.com/", "https://example.test/");
        check(UpdateOffer.Decide(Release("v0.1.9"), installed, false, false, ReleaseFeed.DownloadPrefix).Status == OfferStatus.Available
            && UpdateOffer.Decide(elsewhere, installed, false, false, ReleaseFeed.DownloadPrefix).Status == OfferStatus.NotAvailable,
            "The real feed only downloads from DLB's own GitHub release files.");
        ReleaseInfo traversal = Release("v0.1.9");
        foreach (ReleaseAsset asset in traversal.Assets)
            asset.DownloadUrl = ReleaseFeed.DownloadPrefix + "../../../../attacker/repo/releases/download/v0.1.9/" + asset.Name;
        check(UpdateOffer.Decide(traversal, installed, false, false, ReleaseFeed.DownloadPrefix).Status == OfferStatus.NotAvailable,
            "A download address that climbs out of DLB's releases with ../ is refused.");
        check(UpdateOffer.Decide(Release("v\u0660.\u0661.\u0669"), installed, false, false).Status == OfferStatus.NotAvailable,
            "Tags with non-ASCII digits are refused instead of crashing.");
        ReleaseInfo unc = Release("v0.1.9", scheme: "file");
        foreach (ReleaseAsset asset in unc.Assets) asset.DownloadUrl = "file://server/share/" + asset.Name;
        check(UpdateOffer.Decide(unc, installed, false, true).Status == OfferStatus.NotAvailable,
            "Network-share downloads are refused even for a local test feed.");
    }

    private static void Feeds(Action<bool, string> check)
    {
        const string json = "{\"url\":\"x\",\"author\":{\"login\":\"dlbprecision\",\"id\":1},\"tag_name\":\"v0.1.9\","
            + "\"name\":\"DLB Precision Monitor v0.1.9\",\"body\":null,\"draft\":false,\"prerelease\":false,"
            + "\"created_at\":\"2026-10-01T00:00:00Z\",\"assets\":[{\"name\":\"DLB-Precision-Monitor-0.1.9-Setup.exe\","
            + "\"size\":7434112,\"browser_download_url\":\"https://example.test/a.exe\",\"uploader\":{\"id\":2}}]}";
        ReleaseInfo parsed = ReleaseFeed.Parse(Encoding.UTF8.GetBytes(json));
        check(parsed.Tag == "v0.1.9" && parsed.Name == "DLB Precision Monitor v0.1.9" && parsed.Body == "" && !parsed.Prerelease
            && parsed.Assets.Count == 1 && parsed.Assets[0].Size == 7434112 && parsed.Assets[0].DownloadUrl == "https://example.test/a.exe",
            "GitHub release JSON is read, ignoring fields the updater does not use.");
        bool rejected = false;
        try { ReleaseFeed.Parse(Encoding.UTF8.GetBytes("not json")); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "A malformed release description is rejected.");

        check(ReleaseFeed.FromHttpStatus(404).Status == FeedStatus.NoRelease, "HTTP 404 means there is no Latest release.");
        check(ReleaseFeed.FromHttpStatus(403).Status == FeedStatus.RateLimited && ReleaseFeed.FromHttpStatus(429).Status == FeedStatus.RateLimited,
            "GitHub rate limiting is reported as try again later.");
        check(ReleaseFeed.FromHttpStatus(502).Status == FeedStatus.ServerError, "Server errors are reported separately.");
        check(ReleaseFeed.FromHttpStatus(418).Status == FeedStatus.Invalid, "Unexpected HTTP replies are not treated as releases.");

        string folder = Path.Combine(Path.GetTempPath(), "DlbUpdaterFeed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string local = Path.Combine(folder, "release.json");
            File.WriteAllText(local, json);
            FeedResult fromFile = ReleaseFeed.Fetch(local, "DLB-test");
            check(fromFile.Status == FeedStatus.Release && fromFile.Release?.Tag == "v0.1.9" && ReleaseFeed.IsLocal(local),
                "A local test feed file is read.");
            check(ReleaseFeed.Fetch(Path.Combine(folder, "missing.json"), "DLB-test").Status == FeedStatus.Invalid,
                "A missing local feed is reported, not offered.");
        }
        finally { Directory.Delete(folder, true); }
        check(ReleaseFeed.Fetch("http://example.test/release.json", "DLB-test").Status == FeedStatus.Invalid,
            "Feeds must use HTTPS or a local file.");
        var uncTimer = Stopwatch.StartNew();
        check(ReleaseFeed.Fetch(@"\\updates.example.test\share\release.json", "DLB-test").Status == FeedStatus.Invalid
            && uncTimer.Elapsed < TimeSpan.FromSeconds(2),
            "Network-share feeds are refused without contacting the share.");
        check(ReleaseFeed.Fetch("C:\\bad|path\\release.json", "DLB-test").Status == FeedStatus.Invalid,
            "A malformed feed path is reported instead of crashing.");
        var timer = Stopwatch.StartNew();
        FeedResult unreachable = ReleaseFeed.Fetch("https://127.0.0.1:9/release.json", "DLB-test");
        check(unreachable.Status == FeedStatus.Network && unreachable.Message.Length > 0 && timer.Elapsed < TimeSpan.FromSeconds(20),
            "An unreachable update server gives a friendly network message.");
        check(ReleaseFeed.LatestUrl == "https://api.github.com/repos/dlbprecision/dlb-precision-monitor/releases/latest",
            "The default feed is DLB's own GitHub Latest release.");
    }

    private static void Checksums(Action<bool, string> check)
    {
        string hex = new string('a', 32) + new string('F', 32);
        const string name = "DLB-Precision-Monitor-0.1.9-Setup.exe";
        check(ChecksumFile.TryParse(hex + "  " + name + "\n", name, out string parsed) && parsed == hex.ToLowerInvariant(),
            "A checksum line with the file name is read and normalized.");
        check(ChecksumFile.TryParse("\uFEFF" + hex.ToLowerInvariant(), name, out _), "A bare checksum, even with a byte-order mark, is read.");
        check(ChecksumFile.TryParse(hex + " *" + name, name, out _), "The binary-mode marker before the file name is accepted.");
        check(!ChecksumFile.TryParse(hex + "  other.exe", name, out _), "A checksum for a different file is refused.");
        check(!ChecksumFile.TryParse(hex.Substring(1), name, out _) && !ChecksumFile.TryParse(new string('g', 64), name, out _),
            "Malformed checksums are refused.");
    }

    private static SignatureFacts GoodFacts() => new SignatureFacts
    {
        SignerCommonName = "DLB Precision, LLC",
        SignerOrganization = "DLB Precision, LLC",
        ChainCommonNames = new List<string> { "DLB Precision, LLC", "Microsoft ID Verified CS EOC CA 04",
            "Microsoft ID Verified Code Signing PCA 2021", "Microsoft Identity Verification Root Certificate Authority 2020" },
        CodeSigning = true,
        Timestamped = true,
        SingleSigner = true,
        SignatureValid = true,
        ChainTrusted = true,
        RootThumbprint = PublisherPolicy.MicrosoftIdentityRootThumbprint
    };

    private static void Publishers(Action<bool, string> check)
    {
        check(PublisherPolicy.Evaluate(GoodFacts()) == null, "A timestamped DLB Precision, LLC signature through Microsoft's chain is accepted.");
        SignatureFacts facts = GoodFacts(); facts.SignerCommonName = "DLB Precision LLC";
        check(PublisherPolicy.Evaluate(facts) != null, "A look-alike publisher name is refused.");
        facts = GoodFacts(); facts.SignerCommonName = "DLB Precision, LLC ";
        check(PublisherPolicy.Evaluate(facts) != null, "The publisher name must match exactly.");
        facts = GoodFacts(); facts.SignerOrganization = "Someone Else";
        check(PublisherPolicy.Evaluate(facts) != null, "The organization must also be DLB Precision, LLC.");
        facts = GoodFacts(); facts.ChainCommonNames = new List<string> { "DLB Precision, LLC", "Some Other Root" };
        check(PublisherPolicy.Evaluate(facts) != null, "A certificate outside Microsoft's identity-verified chain is refused.");
        facts = GoodFacts(); facts.CodeSigning = false;
        check(PublisherPolicy.Evaluate(facts) != null, "A certificate not issued for code signing is refused.");
        facts = GoodFacts(); facts.Timestamped = false;
        check(PublisherPolicy.Evaluate(facts) != null, "An untimestamped signature is refused.");
        facts = GoodFacts(); facts.SingleSigner = false;
        check(PublisherPolicy.Evaluate(facts) != null, "A file with more than one signer is refused.");
        facts = GoodFacts(); facts.SignatureValid = false;
        check(PublisherPolicy.Evaluate(facts) != null, "A signer certificate that did not produce the signature is refused.");
        facts = GoodFacts(); facts.ChainTrusted = false;
        check(PublisherPolicy.Evaluate(facts) != null, "A chain that only names Microsoft's root, without Windows trusting it, is refused.");
        facts = GoodFacts(); facts.RootThumbprint = new string('0', 40);
        check(PublisherPolicy.Evaluate(facts) != null, "A different root certificate with Microsoft's name is refused.");
    }

    private static void Outcomes(Action<bool, string> check)
    {
        check(SetupRunner.Interpret(0, versionChanged: true, widgetClosed: true) == SetupOutcome.Updated, "Exit 0 with the new version is a success.");
        check(SetupRunner.Interpret(0, false, false) == SetupOutcome.Failed, "Exit 0 without the new version installed is not reported as success.");
        check(SetupRunner.Interpret(3010, true, true) == SetupOutcome.UpdatedRestartNeeded, "Exit 3010 asks for a restart after updating.");
        check(SetupRunner.Interpret(20, true, true) == SetupOutcome.UpdatedReenableStartup, "DLB's exit 20 reports the startup entry.");
        check(SetupRunner.Interpret(8, false, false) == SetupOutcome.RestartBeforeInstall, "Exit 8 asks for a restart before installing.");
        check(SetupRunner.Interpret(2, false, false) == SetupOutcome.Cancelled && SetupRunner.Interpret(5, false, false) == SetupOutcome.Cancelled,
            "Setup's cancel codes are reported as cancelled.");
        check(SetupRunner.Interpret(1, false, false) == SetupOutcome.Cancelled,
            "A declined Windows prompt, which changes nothing and never closes the widget, is reported as cancelled.");
        check(SetupRunner.Interpret(1, false, true) == SetupOutcome.Failed && SetupRunner.Interpret(4, true, true) == SetupOutcome.Failed,
            "Failures after setup started changing things are reported as failures.");
        check(SetupRunner.Interpret(7, false, false) == SetupOutcome.Failed, "Setup refusing to proceed (exit 7) is a failure, not a cancel.");
        foreach (SetupOutcome outcome in Enum.GetValues(typeof(SetupOutcome)))
            check(SetupRunner.Message(outcome, 7).Length > 0, "Every setup outcome has a message: " + outcome);

        check(SetupRunner.Interpret(2, false, true) == SetupOutcome.Failed && SetupRunner.Interpret(5, true, true) == SetupOutcome.Failed,
            "A cancel code after setup had changed things is a failure.");
        check(SetupRunner.Interpret(3010, false, true) == SetupOutcome.Failed && SetupRunner.Interpret(20, false, true) == SetupOutcome.Failed,
            "Success codes without the new version installed are failures.");
        check(SetupRunner.TaskOptions(startupEnabled: true, desktopShortcut: true) == ""
            && SetupRunner.TaskOptions(false, true) == "/MERGETASKS=\"!startup\""
            && SetupRunner.TaskOptions(true, false) == "/MERGETASKS=\"!desktopicon\""
            && SetupRunner.TaskOptions(false, false) == "/MERGETASKS=\"!startup,!desktopicon\"",
            "An in-app update keeps launch at sign-in and the desktop shortcut as the person left them.");
        RunWithFakeSetup(check);

        string arguments = SetupRunner.Arguments(@"C:\Temp\update setup.log", "/MERGETASKS=\"!startup\"");
        check(arguments.Contains("/SILENT") && arguments.Contains("/SUPPRESSMSGBOXES") && arguments.Contains("/NOCANCEL")
            && arguments.Contains("/NORESTART") && arguments.Contains("/RESTARTEXITCODE=3010") && arguments.Contains("/DLBUPDATE=1")
            && arguments.Contains("/LOG=\"C:\\Temp\\update setup.log\"") && arguments.EndsWith("/MERGETASKS=\"!startup\""),
            "Setup runs silently, never restarts Windows by itself, reports restarts and marks an in-app update.");
    }

    private static void Notes(Action<bool, string> check)
    {
        string text = PlainText.FromMarkdown("## Fixes\r\n\r\n- **Bold** item\n* [the link](https://x.test) and `code`\n\n\n\nLast __line__");
        check(!text.Contains("#") && !text.Contains("**") && !text.Contains("__") && !text.Contains("](") && !text.Contains("`")
            && text.Contains("• Bold item") && text.Contains("• the link and code") && text.Contains("Last line") && !text.Contains("\r\n\r\n\r\n"),
            "Release notes are shown as readable plain text.");
        check(PlainText.FromMarkdown(null) == "", "Missing release notes show nothing.");
        string longText = PlainText.FromMarkdown(new string('x', 10000), 100);
        check(longText.Length <= 100 && longText.EndsWith("…"), "Very long release notes are shortened.");
        var notesTimer = Stopwatch.StartNew();
        PlainText.FromMarkdown(new string('[', 120000));
        check(notesTimer.Elapsed < TimeSpan.FromSeconds(2), "Release notes built to slow down text formatting cannot freeze the updater.");
    }

    private static void Downloads(Action<bool, string> check)
    {
        byte[] data = new byte[3000];
        new Random(7).NextBytes(data);
        long last = 0;
        using (var output = new MemoryStream())
        {
            Downloader.Copy(new MemoryStream(data), output, 3000, Downloader.MaximumBytes, bytes => last = bytes, CancellationToken.None);
            check(output.ToArray().SequenceEqual(data) && last == 3000, "A download is copied completely with progress.");
        }
        check(Throws<InvalidDataException>(() => Downloader.Copy(new MemoryStream(data), new MemoryStream(), 2000, Downloader.MaximumBytes, null, CancellationToken.None)),
            "A download larger than advertised is stopped.");
        check(Throws<InvalidDataException>(() => Downloader.Copy(new MemoryStream(data), new MemoryStream(), 4000, Downloader.MaximumBytes, null, CancellationToken.None)),
            "A download shorter than advertised is refused.");
        check(Throws<InvalidDataException>(() => Downloader.Copy(new MemoryStream(data), new MemoryStream(), 0, 1000, null, CancellationToken.None)),
            "A download over the size limit is stopped.");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            check(Throws<OperationCanceledException>(() => Downloader.Copy(new MemoryStream(data), new MemoryStream(), 3000, Downloader.MaximumBytes, null, cancelled.Token)),
                "Cancel stops a download.");
        }

        string folder = Path.Combine(Path.GetTempPath(), "DlbUpdaterDownload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "source.bin");
            File.WriteAllBytes(source, data);
            var uri = new Uri(source);
            string target = Path.Combine(folder, "target.bin");
            Downloader.Download(uri, target, 3000, allowFile: true, progress: null, CancellationToken.None);
            check(File.ReadAllBytes(target).SequenceEqual(data), "A local test feed asset is downloaded.");
            check(Throws<InvalidDataException>(() => Downloader.Download(uri, Path.Combine(folder, "other.bin"), 3000, false, null, CancellationToken.None)),
                "Local files cannot be downloaded from the real update feed.");
            check(Throws<InvalidDataException>(() => Downloader.Download(new Uri("http://example.test/setup.exe"), Path.Combine(folder, "http.bin"), 3000, false, null, CancellationToken.None))
                && !File.Exists(Path.Combine(folder, "http.bin")),
                "Unencrypted downloads are refused before anything is written.");
            check(Throws<InvalidDataException>(() => Downloader.Download(new Uri("file://server/share/setup.exe"), Path.Combine(folder, "unc.bin"), 3000, true, null, CancellationToken.None)),
                "Network-share downloads are refused.");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void Verification(Action<bool, string> check, bool integration)
    {
        string folder = Path.Combine(Path.GetTempPath(), "DlbUpdaterVerify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string unsigned = Path.Combine(folder, "unsigned.exe");
            File.WriteAllBytes(unsigned, Encoding.ASCII.GetBytes("MZ not really a program"));
            check(!VerifyCopy(unsigned, "1.0").Ok, "An unsigned file is never accepted.");
            VerificationResult mismatch = VerifyCopy(unsigned, "1.0", wrongHash: true);
            check(!mismatch.Ok && mismatch.Reason.IndexOf("checksum", StringComparison.OrdinalIgnoreCase) >= 0,
                "A file that does not match its checksum is refused first.");

            string dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (File.Exists(dotnet))
            {
                VerificationResult wrongPublisher = VerifyCopy(dotnet, FileVersionInfo.GetVersionInfo(dotnet).ProductVersion?.Trim() ?? "");
                // Offline, Windows cannot finish the revocation check and refuses earlier; either way it is refused.
                check(!wrongPublisher.Ok && (wrongPublisher.Retryable || wrongPublisher.Reason.IndexOf("publisher", StringComparison.OrdinalIgnoreCase) >= 0),
                    "A validly signed program from another publisher is refused for that reason: " + wrongPublisher.Reason);
            }

            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DLB Precision Monitor", "DlbPrecision.Monitor.exe");
            if (integration && File.Exists(installed))
            {
                string version = FileVersionInfo.GetVersionInfo(installed).ProductVersion?.Trim() ?? "";
                VerificationResult genuine = VerifyCopy(installed, version);
                check(genuine.Ok, "An installed DLB Precision, LLC program passes verification: " + genuine.Reason);
                check(!VerifyCopy(installed, "9.9.9").Ok, "A genuine DLB program with the wrong version is refused.");
                string tampered = Path.Combine(folder, "tampered.exe");
                byte[] bytes = File.ReadAllBytes(installed);
                bytes[bytes.Length / 2] ^= 0x5A;
                File.WriteAllBytes(tampered, bytes);
                check(!VerifyCopy(tampered, version).Ok, "A byte-tampered DLB program is refused.");
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void RunWithFakeSetup(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "DlbUpdaterSetup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string kept = Path.Combine(folder, "kept", "update-setup.log");
            SetupResult Simulate(int exitCode, bool installs, bool closesWidget, bool reopensWidget, bool writesLog, List<string> reopened,
                Exception? startError = null)
            {
                string version = "0.1.7.9";
                var widgets = new List<int> { 41 };
                var host = new SetupHost
                {
                    InstalledVersion = path => version,
                    RunningWidgets = () => widgets.ToArray(),
                    IsRunning = id => widgets.Contains(id),
                    Reopen = path => reopened.Add(path),
                    StartAndWait = (setup, arguments, working) =>
                    {
                        if (startError != null) throw startError;
                        if (writesLog) File.WriteAllText(Path.Combine(working, "setup.log"), "fake setup log");
                        if (closesWidget) widgets.Remove(41);
                        if (installs) version = "0.1.8.0";
                        if (reopensWidget) widgets.Add(77);
                        return exitCode;
                    }
                };
                if (File.Exists(kept)) File.Delete(kept);
                return SetupRunner.Run(Path.Combine(folder, "setup.exe"), @"C:\Program Files\DLB Precision Monitor", folder, kept, "", host);
            }

            var reopenedWidgets = new List<string>();
            SetupResult declined = Simulate(1, installs: false, closesWidget: false, reopensWidget: false, writesLog: false, reopenedWidgets);
            check(declined.Outcome == SetupOutcome.Cancelled && reopenedWidgets.Count == 0 && declined.KeptLog == null,
                "A declined Windows prompt reports cancelled and leaves the running widget alone.");
            SetupResult updated = Simulate(0, true, true, true, true, reopenedWidgets);
            check(updated.Outcome == SetupOutcome.Updated && reopenedWidgets.Count == 0 && updated.KeptLog == null,
                "A successful update relies on setup reopening the widget and keeps no log.");
            SetupResult failed = Simulate(4, false, true, false, true, reopenedWidgets);
            check(failed.Outcome == SetupOutcome.Failed && reopenedWidgets.Count == 1 && failed.KeptLog == kept && File.Exists(kept),
                "A failed update reopens the closed widget and keeps the setup log.");
            reopenedWidgets.Clear();
            SetupResult earlyFailure = Simulate(1, false, false, false, true, reopenedWidgets);
            check(earlyFailure.Outcome == SetupOutcome.Cancelled && earlyFailure.KeptLog == kept,
                "An update that stops before changing anything still keeps setup's log when it wrote one.");
            SetupResult silentFailure = Simulate(0, false, false, false, true, reopenedWidgets);
            check(silentFailure.Outcome == SetupOutcome.Failed && silentFailure.KeptLog == kept,
                "Setup reporting success without installing the new version is a failure with its log kept.");
            SetupResult refused = Simulate(0, false, false, false, false, reopenedWidgets, new System.ComponentModel.Win32Exception(1223));
            check(refused.Outcome == SetupOutcome.Cancelled, "Declining a prompt shown by Windows before setup starts is reported as cancelled.");
            SetupResult notStarted = Simulate(0, false, false, false, false, reopenedWidgets, new System.ComponentModel.Win32Exception(8));
            check(notStarted.Outcome == SetupOutcome.CouldNotStart && SetupRunner.Message(notStarted.Outcome, notStarted.ExitCode).Contains("couldn't start")
                && notStarted.ExitCode == -1, "A Windows error starting setup is not confused with setup's own exit codes.");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static VerificationResult VerifyCopy(string path, string expectedVersion, bool wrongHash = false)
    {
        string hash;
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
            hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        if (wrongHash) hash = new string('0', 64);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            return PackageVerifier.Verify(locked, path, hash, expectedVersion);
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}
