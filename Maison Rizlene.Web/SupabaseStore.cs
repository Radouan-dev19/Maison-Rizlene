using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Maison_Rizlene.Web;

public sealed class SupabaseStore(HttpClient client, IConfiguration config)
{
    private string Url => (config["SUPABASE_URL"] ?? throw new InvalidOperationException("SUPABASE_URL manquant")).TrimEnd('/');
    private string ServiceKey => config["SUPABASE_SECRET_KEY"] ?? config["SUPABASE_SERVICE_ROLE_KEY"] ?? throw new InvalidOperationException("SUPABASE_SECRET_KEY manquant");
    private string AnonKey => config["SUPABASE_PUBLISHABLE_KEY"] ?? config["SUPABASE_ANON_KEY"] ?? throw new InvalidOperationException("SUPABASE_PUBLISHABLE_KEY manquant");
    private const string Bucket = "project-previews";

    private HttpRequestMessage Request(HttpMethod method, string path, bool service = true)
    {
        var request = new HttpRequestMessage(method, Url + path);
        request.Headers.UserAgent.ParseAdd("MaisonRizleneServer/1.0");
        var key = service ? ServiceKey : AnonKey;
        request.Headers.Add("apikey", key);
        if (!key.StartsWith("sb_", StringComparison.Ordinal))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }

    private async Task<JsonDocument> JsonAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await client.SendAsync(request, ct))
        {
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Supabase HTTP {(int)response.StatusCode}");
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
    }

    public async Task<(Guid Id, string Email)?> AuthenticateAdminAsync(string email, string password, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, "/auth/v1/token?grant_type=password", false);
        request.Content = JsonContent.Create(new { email, password });
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!result.RootElement.TryGetProperty("user", out var user) || !Guid.TryParse(user.GetProperty("id").GetString(), out var id)) return null;
        return await IsAdminAsync(id, ct) ? (id, email) : null;
    }

    public async Task<bool> SetInvitedPasswordAsync(string accessToken, string password, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Put, "/auth/v1/user", false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new { password });
        using var response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> IsAdminAsync(Guid id, CancellationToken ct)
    {
        using var result = await JsonAsync(Request(HttpMethod.Get, $"/rest/v1/admins?id=eq.{id}&select=id&limit=1"), ct);
        return result.RootElement.GetArrayLength() == 1;
    }

    public async Task<Project?> RedeemAsync(string codeHash, string ticketHash, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, "/rest/v1/rpc/redeem_project");
        request.Content = JsonContent.Create(new { p_code_hash = codeHash, p_ticket_hash = ticketHash });
        using var result = await JsonAsync(request, ct);
        var rows = result.RootElement;
        if (rows.GetArrayLength() == 0) return null;
        var row = rows[0];
        return new Project(row.GetProperty("id").GetGuid(), row.GetProperty("name").GetString() ?? "", "", null, null);
    }

    public async Task<string?> ConsumeVideoAsync(Guid id, string ticketHash, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, "/rest/v1/rpc/consume_video");
        request.Content = JsonContent.Create(new { p_project_id = id, p_ticket_hash = ticketHash });
        using var result = await JsonAsync(request, ct);
        return result.RootElement.GetArrayLength() == 0 ? null : result.RootElement[0].GetProperty("video_path").GetString();
    }

    public async Task<HttpResponseMessage> OpenVideoAsync(string path, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, $"/storage/v1/object/authenticated/{Bucket}/{Uri.EscapeDataString(path)}");
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    public async Task UploadVideoAsync(string path, Stream video, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, $"/storage/v1/object/{Bucket}/{Uri.EscapeDataString(path)}");
        request.Content = new StreamContent(video);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Upload Supabase HTTP {(int)response.StatusCode}");
    }

    public async Task DeleteVideoAsync(string path, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Delete, $"/storage/v1/object/{Bucket}/{Uri.EscapeDataString(path)}");
        using var response = await client.SendAsync(request, ct);
    }

    public async Task<Project> CreateProjectAsync(string name, string clientName, string path, string codeHash, Guid adminId, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, "/rest/v1/projects?select=id,name,client_name,created_at,viewed_at");
        request.Headers.Add("Prefer", "return=representation");
        request.Content = JsonContent.Create(new { name, client_name = clientName, video_path = path, access_code_hash = codeHash, created_by = adminId });
        using var result = await JsonAsync(request, ct);
        return ParseProject(result.RootElement[0]);
    }

    public async Task<List<Project>> ListProjectsAsync(CancellationToken ct)
    {
        using var result = await JsonAsync(Request(HttpMethod.Get, "/rest/v1/projects?select=id,name,client_name,created_at,viewed_at&order=created_at.desc"), ct);
        return result.RootElement.EnumerateArray().Select(ParseProject).ToList();
    }

    private static Project ParseProject(JsonElement row) => new(
        row.GetProperty("id").GetGuid(),
        row.GetProperty("name").GetString() ?? "",
        row.GetProperty("client_name").GetString() ?? "",
        row.GetProperty("created_at").GetDateTimeOffset(),
        row.GetProperty("viewed_at").ValueKind == JsonValueKind.Null ? null : row.GetProperty("viewed_at").GetDateTimeOffset());
}

public sealed record Project(Guid Id, string Name, string ClientName, DateTimeOffset? CreatedAt, DateTimeOffset? ViewedAt);
