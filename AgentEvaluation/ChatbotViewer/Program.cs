using ChatbotViewer.Components;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Chatbot の URL を設定ファイルから取得
var chatbotUrl = builder.Configuration["ChatbotUrl"] ?? "http://chatbot-svc";

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Chatbot API 呼び出し用の HttpClient を登録
builder.Services.AddHttpClient("chatbot", client =>
{
    client.BaseAddress = new Uri(chatbotUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// OpenTelemetry の設定
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("ChatbotViewer"))
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
