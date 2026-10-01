using System;
using System.IO;
using System.Net;
using System.Threading;

namespace DlbPrecision.Updater
{
    internal static class Downloader
    {
        // DLB installers are about 7 MB; anything near this limit is not a DLB installer.
        public const long MaximumBytes = 64L * 1024 * 1024;
        // A download that makes no progress for this long is treated as stalled.
        private const int StallTimeoutMilliseconds = 30000;

        public static void Download(Uri source, string destination, long expectedSize, bool allowFile, Action<long>? progress,
            CancellationToken token, string userAgent = "DLBPrecisionMonitor-Updater")
        {
            if (source.IsFile && !allowFile) throw new InvalidDataException("Local files can only be used by a local test feed.");
            if (!source.IsFile && source.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Updates are only downloaded over a secure connection.");
            try
            {
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (source.IsFile)
                    {
                        using (var input = new FileStream(source.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                            Copy(input, output, expectedSize, MaximumBytes, progress, token);
                        return;
                    }
                    var request = (HttpWebRequest)WebRequest.Create(source);
                    request.UserAgent = userAgent;
                    request.Timeout = StallTimeoutMilliseconds;
                    request.ReadWriteTimeout = StallTimeoutMilliseconds;
                    request.AllowAutoRedirect = true;
                    request.MaximumAutomaticRedirections = 5;
                    using (token.Register(request.Abort))
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        // GitHub redirects release assets to its download host; never accept a downgrade to plain HTTP.
                        if (response.ResponseUri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("The download was redirected to an insecure address.");
                        using (Stream input = response.GetResponseStream())
                            Copy(input, output, expectedSize, MaximumBytes, progress, token);
                    }
                }
            }
            catch (WebException) when (token.IsCancellationRequested)
            {
                DeletePartial(destination);
                throw new OperationCanceledException(token);
            }
            catch
            {
                DeletePartial(destination);
                throw;
            }
        }

        public static void Copy(Stream input, Stream output, long expectedSize, long maximumBytes, Action<long>? progress, CancellationToken token)
        {
            var buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                token.ThrowIfCancellationRequested();
                total += read;
                if (total > maximumBytes) throw new InvalidDataException("The download is larger than any DLB installer.");
                if (expectedSize > 0 && total > expectedSize) throw new InvalidDataException("The download is larger than the release says it should be.");
                output.Write(buffer, 0, read);
                progress?.Invoke(total);
            }
            if (expectedSize > 0 && total != expectedSize) throw new InvalidDataException("The download ended early. Try again.");
        }

        private static void DeletePartial(string destination)
        {
            try { if (File.Exists(destination)) File.Delete(destination); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                // The temporary folder is removed later; the original download error is the one to report.
            }
        }
    }
}
