using AgentCardViewer.Components;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Dispatcher の URL を設定ファイルから取得
var dispatcherUrl = builder.Configuration["DispatcherUrl"] ?? "http://a2a-dispatcher-svc";

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Dispatcher API 呼び出し用の HttpClient を登録
builder.Services.AddHttpClient("dispatcher", client =>
{
    client.BaseAddress = new Uri(dispatcherUrl);
});

// OpenTelemetry の設定
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("AgentCardViewer"))
    .WithTracing(tracing => tracing
        .AddSource("*")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter());

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
