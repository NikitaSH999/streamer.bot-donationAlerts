using System;
using System.Net;
using Newtonsoft.Json;
using System.Collections.Generic;

///----------------------------------------------------------------------------
///   Module:     DonationAlerts Integration for Streamer.bot
///   Repository: (set after publishing)
///----------------------------------------------------------------------------
public class CPHInline
{
    private const string CurrentVersion = "2.0.0";
    private const string DefaultRepoSlug = "";

    public bool IsUpdateAvailable()
    {
        return GetNewerGitHubVersion(CurrentVersion) != null;
    }

    public bool CheckAndAnnounce()
    {
        var newerVersion = GetNewerGitHubVersion(CurrentVersion);
        if (newerVersion != null)
            CPH.SendMessage(string.Format("DonationAlerts update available: {0} - {1}", newerVersion.TagName, newerVersion.HtmlUrl));

        return newerVersion != null;
    }

    private GitHubReleaseResponse GetNewerGitHubVersion(string currentVersion)
    {
        var repoSlug = GetRepoSlug();
        if (string.IsNullOrWhiteSpace(repoSlug))
        {
            CPH.LogDebug("UpdateChecker: daUpdateRepo is empty; skipping");
            return null;
        }

        var newer = FetchLatestGitHubVersion(repoSlug);
        if (newer == null)
            return null;

        var current = ParseVersion(currentVersion);
        var available = ParseVersion(newer.TagName);
        if (current == null || available == null)
            return null;

        if (current >= available)
            return null;

        return newer;
    }

    private string GetRepoSlug()
    {
        var slug = CPH.GetGlobalVar<string>("daUpdateRepo");
        if (!string.IsNullOrWhiteSpace(slug))
            return slug.Trim();

        return DefaultRepoSlug;
    }

    private GitHubReleaseResponse FetchLatestGitHubVersion(string repoSlug)
    {
        try
        {
            var releases = GetGitHubReleaseVersionsAsync(repoSlug);
            foreach (var release in releases)
            {
                if (release.IsDraft || release.IsPreRelease)
                    continue;

                return release;
            }
        }
        catch (Exception) {}

        return null;
    }

    private List<GitHubReleaseResponse> GetGitHubReleaseVersionsAsync(string repoSlug)
    {
        try
        {
            WebClient webClient = new WebClient();
            webClient.Headers.Add("User-Agent", "StreamerBot-DonationAlerts");
            Uri uri = new Uri(string.Format("https://api.github.com/repos/{0}/releases?per_page=100", repoSlug));
            string releases = webClient.DownloadString(uri);

            return JsonConvert.DeserializeObject<List<GitHubReleaseResponse>>(releases);
        }
        catch (Exception) {}
        return new List<GitHubReleaseResponse>();
    }

    private Version ParseVersion(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        string cleaned = input.Trim();
        if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned.Substring(1);

        if (Version.TryParse(cleaned, out Version version))
            return version;

        return null;
    }

    private class GitHubReleaseResponse
    {
        [JsonProperty("html_url")]
        public string HtmlUrl { get; set; }
        [JsonProperty("tag_name")]
        public string TagName { get; set; }
        [JsonProperty("name")]
        public string Name { get; set; }
        [JsonProperty("prerelease")]
        public bool IsPreRelease { get; set; }
        [JsonProperty("draft")]
        public bool IsDraft { get; set; }
    }
}
