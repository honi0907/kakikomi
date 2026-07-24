using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kakikomi.Updates;

internal sealed class GitHubReleaseClient
{
    private static readonly HttpClient Http = CreateHttpClient();

    public async Task<GitHubReleaseDto?> GetReleaseByTagAsync(
        string repo,
        string tag,
        string? token,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{repo}/releases/tags/{Uri.EscapeDataString(tag)}";
        return await GetJsonAsync<GitHubReleaseDto>(url, token, cancellationToken);
    }

    public async Task<IReadOnlyList<GitHubReleaseDto>> ListReleasesAsync(
        string repo,
        string? token,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{repo}/releases?per_page=100";
        var list = await GetJsonAsync<List<GitHubReleaseDto>>(url, token, cancellationToken);
        return list ?? [];
    }

    public async Task DownloadAsync(
        string downloadUrl,
        string destinationPath,
        string? token,
        IProgress<OnlineUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        ApplyHeaders(request, token);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(destinationPath);

        var buffer = new byte[81920];
        long received = 0;

        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;

            if (total is > 0)
            {
                var percent = received * 100.0 / total.Value;
                progress?.Report(new OnlineUpdateProgress($"ダウンロード中... {percent:0}%", percent));
            }
            else
            {
                var pseudo = Math.Min(99, received / (1024.0 * 1024.0));
                progress?.Report(new OnlineUpdateProgress($"ダウンロード中... {pseudo:0}%", pseudo));
            }
        }

        progress?.Report(new OnlineUpdateProgress("ダウンロード完了", 100));
    }

    private static async Task<T?> GetJsonAsync<T>(string url, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(request, token);

        using var response = await Http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return default;

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private static void ApplyHeaders(HttpRequestMessage request, string? token)
    {
        request.Headers.UserAgent.ParseAdd("kakikomi-updater");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static HttpClient CreateHttpClient() =>
        new() { Timeout = TimeSpan.FromMinutes(10) };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

internal sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubReleaseAssetDto>? Assets { get; set; }
}

internal sealed class GitHubReleaseAssetDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }
}
