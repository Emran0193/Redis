using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using MudBlazor.Services;
using NovaDB.Admin.Auth;
using NovaDB.Admin.Services;
using NovaDB.Contracts.Admin;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Cleartext gRPC (h2c) to NovaDB.Server GrpcPort.
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddMudServices();
builder.Services.AddSingleton<IAdminAuditLog, AdminAuditLog>();
builder.Services.AddSingleton<AdminLogBuffer>();
builder.Services.AddHostedService<LiveTelemetryFeeder>();
builder.Services.AddScoped<ThemeState>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.Name = "novadb.admin.auth";
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AdminPolicies.ReadOnly, p => p.RequireRole(
        AdminRoles.Admin, AdminRoles.Operator, AdminRoles.ReadOnly));
    options.AddPolicy(AdminPolicies.Operator, p => p.RequireRole(
        AdminRoles.Admin, AdminRoles.Operator));
    options.AddPolicy(AdminPolicies.Admin, p => p.RequireRole(AdminRoles.Admin));
});

var grpcAddress = builder.Configuration["NovaDB:AdminGrpc:Address"] ?? "http://127.0.0.1:7381";
void AddAdminGrpcClient<T>() where T : class
    => builder.Services.AddGrpcClient<T>(o => o.Address = new Uri(grpcAddress))
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            AllowAutoRedirect = false
        })
        .ConfigureChannel(c =>
        {
            c.HttpVersion = System.Net.HttpVersion.Version20;
            c.HttpVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact;
        });

AddAdminGrpcClient<MetricsService.MetricsServiceClient>();
AddAdminGrpcClient<KeyService.KeyServiceClient>();
AddAdminGrpcClient<ClientService.ClientServiceClient>();
AddAdminGrpcClient<ConfigService.ConfigServiceClient>();
AddAdminGrpcClient<HealthService.HealthServiceClient>();
AddAdminGrpcClient<PersistenceService.PersistenceServiceClient>();
AddAdminGrpcClient<PubSubService.PubSubServiceClient>();
AddAdminGrpcClient<HistoryService.HistoryServiceClient>();
AddAdminGrpcClient<ChaosService.ChaosServiceClient>();
AddAdminGrpcClient<DiagnosticsService.DiagnosticsServiceClient>();
AddAdminGrpcClient<ReplicationService.ReplicationServiceClient>();
AddAdminGrpcClient<PerformanceService.PerformanceServiceClient>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/account/login", async (HttpContext http, IConfiguration config) =>
{
    var form = await http.Request.ReadFormAsync().ConfigureAwait(false);
    var user = form["username"].ToString();
    var password = form["password"].ToString();
    var role = AdminCredentialStore.TryAuthenticate(config, user, password);
    if (role is null)
    {
        return Results.Redirect("/login?error=1");
    }

    var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
    identity.AddClaim(new Claim(ClaimTypes.Name, user));
    identity.AddClaim(new Claim(ClaimTypes.Role, role));
    await http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity)).ConfigureAwait(false);
    return Results.Redirect("/");
});

app.MapPost("/account/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
    return Results.Redirect("/login");
});

app.MapBlazorHub();
app.MapHub<LiveHub>("/hubs/live");
app.MapFallbackToPage("/_Host");
app.MapDefaultEndpoints();

await app.RunAsync().ConfigureAwait(false);
