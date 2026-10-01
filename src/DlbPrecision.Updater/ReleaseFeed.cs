using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace DlbPrecision.Updater
{
    // Only the GitHub release fields the updater uses; every other field in the reply is ignored.
    [DataContract]
    internal sealed class ReleaseInfo
    {
        [DataMember(Name = "tag_name")] public string Tag { get; set; } = "";
        [DataMember(Name = "name")] public string Name { get; set; } = "";
        [DataMember(Name = "body")] public string Body { get; set; } = "";
        [DataMember(Name = "draft")] public bool Draft { get; set; }
        [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
        [DataMember(Name = "assets")] public List<ReleaseAsset> Assets { get; set; } = new List<ReleaseAsset>();
    }

    [DataContract]
    internal sealed class ReleaseAsset
    {
        [DataMember(Name = "name")] public string Name { get; set; } = "";
        [DataMember(Name = "size")] public long Size { get; set; }
        [DataMember(Name = "browser_download_url")] public string DownloadUrl { get; set; } = "";
    }

    internal enum FeedStatus { Release, NoRelease, RateLimited, ServerError, Network, Invalid }

    internal sealed class FeedResult
    {
        public FeedResult(FeedStatus status, ReleaseInfo? release, string message)
        {
            Status = status;
            Release = release;
            Message = message;
        }

        public FeedStatus Status { get; }
        public ReleaseInfo? Release { get; }
        public string Message { get; }
    }

    internal static class ReleaseFeed
    {
        public const string LatestUrl = "https://api.github.com/repos/dlbprecision/dlb-precision-monitor/releases/latest";
        private const int MaximumBytes = 1024 * 1024;
        private const int TimeoutMilliseconds = 15000;
        private const string NetworkMessage = "Couldn't reach the update server. Check your internet connection.";

        public static bool IsLocal(string source) => !Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) || uri.IsFile;

        // One request per check. A local file is accepted only as an explicit --feed for testing.
        public static FeedResult Fetch(string source, string userAgent)
        {
            try
            {
                if (IsLocal(source))
                {
                    string path = Uri.TryCreate(source, UriKind.Absolute, out Uri? fileUri) ? fileUri.LocalPath : Path.GetFullPath(source);
                    var file = new FileInfo(path);
                    if (!file.Exists) return new FeedResult(FeedStatus.Invalid, null, "The test update feed file was not found.");
                    if (file.Length > MaximumBytes) return new FeedResult(FeedStatus.Invalid, null, "The test update feed file is too large.");
                    return Found(Parse(File.ReadAllBytes(path)));
                }
                var uri = new Uri(source);
                if (uri.Scheme != Uri.UriSchemeHttps)
                    return new FeedResult(FeedStatus.Invalid, null, "Updates are only checked over a secure connection.");
                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.UserAgent = userAgent;
                request.Accept = "application/vnd.github+json";
                request.Timeout = TimeoutMilliseconds;
                request.ReadWriteTimeout = TimeoutMilliseconds;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (Stream body = response.GetResponseStream())
                using (var buffer = new MemoryStream())
                {
                    var chunk = new byte[16384];
                    int read;
                    while ((read = body.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (buffer.Length + read > MaximumBytes) return new FeedResult(FeedStatus.Invalid, null, "The update information is unexpectedly large.");
                        buffer.Write(chunk, 0, read);
                    }
                    return Found(Parse(buffer.ToArray()));
                }
            }
            catch (WebException error) when (error.Response is HttpWebResponse response)
            {
                using (response) return FromHttpStatus((int)response.StatusCode);
            }
            catch (WebException error) when (error.Status == WebExceptionStatus.TrustFailure || error.Status == WebExceptionStatus.SecureChannelFailure)
            {
                return new FeedResult(FeedStatus.Network, null,
                    "Couldn't make a secure connection to the update server. Check your internet connection and that this PC's date and time are correct.");
            }
            catch (WebException)
            {
                return new FeedResult(FeedStatus.Network, null, NetworkMessage);
            }
            catch (InvalidDataException)
            {
                return new FeedResult(FeedStatus.Invalid, null, "The update information could not be read.");
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is UriFormatException)
            {
                return new FeedResult(FeedStatus.Invalid, null, "The update information could not be read.");
            }
        }

        public static FeedResult FromHttpStatus(int status)
        {
            if (status == 404) return new FeedResult(FeedStatus.NoRelease, null, "No update has been published yet.");
            if (status == 403 || status == 429)
                return new FeedResult(FeedStatus.RateLimited, null, "Too many update checks right now. Try again in a few minutes.");
            if (status >= 500) return new FeedResult(FeedStatus.ServerError, null, "The update server is having trouble. Try again later.");
            return new FeedResult(FeedStatus.Invalid, null, "The update server gave an unexpected reply (HTTP " + status + ").");
        }

        public static ReleaseInfo Parse(byte[] json)
        {
            if (json.Length == 0 || json.Length > MaximumBytes) throw new InvalidDataException("The release description has an invalid size.");
            try
            {
                using (var stream = new MemoryStream(json, false))
                {
                    var release = (ReleaseInfo?)new DataContractJsonSerializer(typeof(ReleaseInfo)).ReadObject(stream)
                        ?? throw new InvalidDataException("The release description is empty.");
                    // The serializer skips initializers, so absent or null fields arrive as null.
                    release.Tag = release.Tag ?? "";
                    release.Name = release.Name ?? "";
                    release.Body = release.Body ?? "";
                    release.Assets = release.Assets ?? new List<ReleaseAsset>();
                    release.Assets.RemoveAll(asset => asset == null);
                    foreach (ReleaseAsset asset in release.Assets)
                    {
                        asset.Name = asset.Name ?? "";
                        asset.DownloadUrl = asset.DownloadUrl ?? "";
                    }
                    return release;
                }
            }
            catch (Exception error) when (error is SerializationException || error is System.Xml.XmlException)
            {
                throw new InvalidDataException("The release description is not valid.", error);
            }
        }

        private static FeedResult Found(ReleaseInfo release) => new FeedResult(FeedStatus.Release, release, "");
    }
}
