using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Maison_Rizlene.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsDevelopment())
{
    var localSettings = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", ".env.local"));
    if (File.Exists(localSettings))
    {
        foreach (var line in File.ReadLines(localSettings))
        {
            var entry = line.Trim();
            if (entry.Length == 0 || entry.StartsWith('#')) continue;
            var separator = entry.IndexOf('=');
            if (separator <= 0) continue;
            var name = entry[..separator].Trim();
            if (name is "SUPABASE_URL" or "SUPABASE_PUBLISHABLE_KEY" or "SUPABASE_SECRET_KEY")
                builder.Configuration[name] = entry[(separator + 1)..].Trim();
        }
    }
}
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
if (OperatingSystem.IsWindows() && builder.Environment.IsDevelopment())
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, ".local-keys")));
builder.Services.AddHttpClient<SupabaseStore>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "maisonrizlene.admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
        options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("access", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 8, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 5, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
});
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 25 * 1024 * 1024);

var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self' https://fonts.googleapis.com; font-src https://fonts.gstatic.com; media-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
    await next();
});
if (!app.Environment.IsDevelopment()) { app.UseExceptionHandler("/error"); app.UseHsts(); }
app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();
app.MapGet("/error", () => Results.Problem("Une erreur est survenue.", statusCode: 500));
app.MapGet("/api/csrf", (IAntiforgery antiforgery, HttpContext context) =>
{
    var token = antiforgery.GetAndStoreTokens(context).RequestToken;
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new { token });
});

app.MapPost("/api/access", async (AccessRequest request, SupabaseStore store, CancellationToken ct) =>
{
    var code = request.Code?.Trim() ?? "";
    if (code.Length is < 20 or > 100) return Results.BadRequest(new { error = "Code invalide ou déjà utilisé." });
    var ticket = CryptoCode.New();
    var project = await store.RedeemAsync(CryptoCode.Hash(code), CryptoCode.Hash(ticket), ct);
    return project is null ? Results.BadRequest(new { error = "Code invalide ou déjà utilisé." })
        : Results.Ok(new { project.Id, project.Name, ticket });
}).RequireRateLimiting("access").ValidateAntiforgery();

app.MapGet("/api/video/{id:guid}", async (Guid id, string? ticket, SupabaseStore store, HttpContext context, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    if (string.IsNullOrWhiteSpace(ticket) || ticket.Length > 100) return Results.NotFound();
    var path = await store.ConsumeVideoAsync(id, CryptoCode.Hash(ticket), ct);
    if (path is null) return Results.NotFound();
    using var source = await store.OpenVideoAsync(path, ct);
    if (!source.IsSuccessStatusCode) return Results.Problem("Vidéo indisponible.", statusCode: 502);
    context.Response.ContentType = "video/mp4";
    if (source.Content.Headers.ContentLength is long length) context.Response.ContentLength = length;
    await using var stream = await source.Content.ReadAsStreamAsync(ct);
    await stream.CopyToAsync(context.Response.Body, ct);
    return Results.Empty;
});

app.MapPost("/api/admin/login", async (LoginRequest request, SupabaseStore store, HttpContext context, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password)) return Results.Unauthorized();
    var admin = await store.AuthenticateAdminAsync(request.Email.Trim(), request.Password, ct);
    if (admin is null) return Results.Unauthorized();
    var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, admin.Value.Id.ToString()), new Claim(ClaimTypes.Email, admin.Value.Email)], CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Ok(new { email = admin.Value.Email });
}).RequireRateLimiting("login").ValidateAntiforgery();

app.MapPost("/api/admin/set-password", async (SetPasswordRequest request, SupabaseStore store, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.AccessToken) || request.Password is null || request.Password.Length < 12)
        return Results.BadRequest(new { error = "Choisissez un mot de passe d’au moins 12 caractères." });
    return await store.SetInvitedPasswordAsync(request.AccessToken, request.Password, ct)
        ? Results.Ok() : Results.BadRequest(new { error = "Invitation expirée ou invalide. Demandez une nouvelle invitation." });
}).RequireRateLimiting("login").ValidateAntiforgery();

app.MapPost("/api/admin/logout", async (HttpContext context) =>
{
    await context.SignOutAsync();
    return Results.Ok();
}).RequireAuthorization().ValidateAntiforgery();

app.MapGet("/api/admin/me", async (HttpContext context, SupabaseStore store, CancellationToken ct) =>
{
    var id = Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    return await store.IsAdminAsync(id, ct) ? Results.Ok(new { email = context.User.FindFirstValue(ClaimTypes.Email) }) : Results.Forbid();
}).RequireAuthorization();

app.MapGet("/api/admin/projects", async (HttpContext context, SupabaseStore store, CancellationToken ct) =>
{
    var id = Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    return await store.IsAdminAsync(id, ct) ? Results.Ok(await store.ListProjectsAsync(ct)) : Results.Forbid();
}).RequireAuthorization();

app.MapPost("/api/admin/projects", async (HttpContext context, SupabaseStore store, CancellationToken ct) =>
{
    var id = Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    if (!await store.IsAdminAsync(id, ct)) return Results.Forbid();
    var form = await context.Request.ReadFormAsync(ct);
    var name = form["name"].ToString().Trim();
    var clientName = form["clientName"].ToString().Trim();
    var video = form.Files.GetFile("video");
    if (name.Length is < 2 or > 120 || clientName.Length is < 2 or > 120 || video is null || video.Length is < 1 or > 25 * 1024 * 1024)
        return Results.BadRequest(new { error = "Renseignez le projet, le client et une vidéo MP4 de 25 Mo maximum." });
    if (!video.FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || video.ContentType != "video/mp4")
        return Results.BadRequest(new { error = "Seules les vidéos MP4 sont acceptées." });
    var path = $"{Guid.NewGuid():N}.mp4";
    await using var stream = video.OpenReadStream();
    await store.UploadVideoAsync(path, stream, ct);
    var code = CryptoCode.New();
    try
    {
        var project = await store.CreateProjectAsync(name, clientName, path, CryptoCode.Hash(code), id, ct);
        return Results.Ok(new { project.Id, project.Name, project.ClientName, code });
    }
    catch
    {
        await store.DeleteVideoAsync(path, ct);
        throw;
    }
}).RequireAuthorization().ValidateAntiforgery();

app.MapFallbackToFile("index.html");
app.Run();

record AccessRequest(string? Code);
record LoginRequest(string? Email, string? Password);
record SetPasswordRequest(string? AccessToken, string? Password);

static class CryptoCode
{
    public static string New() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

static class AntiforgeryEndpointExtensions
{
    public static RouteHandlerBuilder ValidateAntiforgery(this RouteHandlerBuilder route) => route.AddEndpointFilter(async (context, next) =>
    {
        try
        {
            await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext);
            return await next(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest(new { error = "Session expirée. Rechargez la page." });
        }
    });
}
