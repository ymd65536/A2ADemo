using AgentCardViewer.Components;

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
