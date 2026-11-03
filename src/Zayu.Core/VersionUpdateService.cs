using System;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Zayu.Core
{
    public sealed class VersionUpdateResult
    {
        public bool HasUpdate { get; set; }
        public string CurrentVersion { get; set; }
        public string LatestVersion { get; set; }
        public string ReleaseUrl { get; set; }
        public string Error { get; set; }
        public bool HasReleaseInfo { get; set; }

        public VersionUpdateResult()
        {
            CurrentVersion = string.Empty;
            LatestVersion = string.Empty;
            ReleaseUrl = string.Empty;
            Error = string.Empty;
        }
    }

    /// <summary>从 GitHub 获取最新 Release / Tag，判断是否有新版本。</summary>
    public static class VersionUpdateService
    {
        public const string Repository = "BlackShinex/ZayuToolbox";
        public const string ReleasesApi = "https://api.github.com/repos/" + Repository + "/releases?per_page=10";
        public const string TagsApi = "https://api.github.com/repos/" + Repository + "/tags?per_page=10";
        public const string ReleasesPage = "https://github.com/" + Repository + "/releases";

        static VersionUpdateService()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch
            {
            }
        }

        public static async Task<VersionUpdateResult> CheckAsync(string currentVersion,
            CancellationToken cancellationToken)
        {
            VersionUpdateResult result = new VersionUpdateResult();
            result.CurrentVersion = NormalizeVersion(currentVersion);

            JsonResponse release = await GetJsonAsync(ReleasesApi, cancellationToken)
                .ConfigureAwait(false);
            string error = release.Error;
            string releaseJson = release.Json;
            if (!string.IsNullOrEmpty(releaseJson))
            {
                string tag = JsonString(releaseJson, "tag_name");
                string url = JsonString(releaseJson, "html_url");
                bool draft = JsonBool(releaseJson, "draft");
                bool prerelease = JsonBool(releaseJson, "prerelease");
                if (!string.IsNullOrEmpty(tag) && !draft && !prerelease)
                {
                    FillResult(result, tag, url);
                    return result;
                }
            }

            JsonResponse tags = await GetJsonAsync(TagsApi, cancellationToken)
                .ConfigureAwait(false);
            error = tags.Error;
            string tagsJson = tags.Json;
            if (!string.IsNullOrEmpty(tagsJson))
            {
                string tag = JsonString(tagsJson, "name");
                if (!string.IsNullOrEmpty(tag))
                {
                    FillResult(result, tag, ReleasesPage);
                    result.Error = string.Empty;
                    return result;
                }
            }

            result.Error = string.IsNullOrEmpty(error) ? "暂无发布版本" : error;
            return result;
        }

        private static void FillResult(VersionUpdateResult result, string tag, string url)
        {
            result.HasReleaseInfo = true;
            result.LatestVersion = NormalizeVersion(tag);
            result.HasUpdate = IsNewer(result.LatestVersion, result.CurrentVersion);
            result.ReleaseUrl = string.IsNullOrEmpty(url) ? ReleasesPage : url.Replace("\\/", "/");
        }

        public static bool IsNewer(string candidate, string current)
        {
            int[] left = ParseVersion(candidate);
            int[] right = ParseVersion(current);
            int length = Math.Max(left.Length, right.Length);
            for (int i = 0; i < length; i++)
            {
                int a = i < left.Length ? left[i] : 0;
                int b = i < right.Length ? right[i] : 0;
                if (a != b) return a > b;
            }
            return false;
        }

        public static string NormalizeVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return string.Empty;
            string text = version.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            int cut = text.IndexOfAny(new char[] { '-', '+', ' ' });
            if (cut >= 0) text = text.Substring(0, cut);
            return text;
        }

        private static int[] ParseVersion(string version)
        {
            string normalized = NormalizeVersion(version);
            string[] parts = normalized.Split('.');
            int[] values = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                int value;
                int.TryParse(parts[i], out value);
                values[i] = value;
            }
            return values;
        }

        private static async Task<JsonResponse> GetJsonAsync(string url, CancellationToken cancellationToken)
        {
            JsonResponse result = new JsonResponse();
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(15);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(
                        "ZayuToolbox/1.8 (Windows NT 10.0; Win64; x64)");
                    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                    using (HttpResponseMessage response = await client.GetAsync(url, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        if (response.StatusCode == HttpStatusCode.NotFound)
                        {
                            result.Error = "暂无发布版本";
                            return result;
                        }
                        if (!response.IsSuccessStatusCode)
                        {
                            result.Error = "GitHub 返回 HTTP " + (int)response.StatusCode;
                            return result;
                        }
                        result.Json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return result;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Error = "无法连接 GitHub：" + ex.Message;
                return result;
            }
        }

        private sealed class JsonResponse
        {
            public string Json = string.Empty;
            public string Error = string.Empty;
        }

        private static string JsonString(string json, string key)
        {
            Match match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static bool JsonBool(string json, string key)
        {
            Match match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)");
            return match.Success && string.Equals(match.Groups[1].Value, "true",
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
