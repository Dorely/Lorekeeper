using Lorekeeper.AiConsole;
using Lorekeeper.Auth;
using Lorekeeper.Chapters;
using Lorekeeper.Components;
using Lorekeeper.Context;
using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Projects;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpClient();

// Persistence
builder.Services.AddLorekeeperPersistence(builder.Configuration);
builder.Services.AddScoped<ILlmProviderRepository, LlmProviderRepository>();
builder.Services.AddScoped<IOAuthTokenRepository, OAuthTokenRepository>();
builder.Services.AddScoped<IProjectRepository, ProjectRepository>();
builder.Services.AddScoped<IGraphNodeRepository, GraphNodeRepository>();
builder.Services.AddScoped<IGraphEdgeRepository, GraphEdgeRepository>();
builder.Services.AddScoped<IChapterRepository, ChapterRepository>();
builder.Services.AddScoped<IActRepository, ActRepository>();
builder.Services.AddScoped<IOutlineConversationRepository, OutlineConversationRepository>();

// Knowledge
builder.Services.AddScoped<IVectorStore, SqliteVecVectorStore>();
builder.Services.AddScoped<IGraphStore, RelationalGraphStore>();
builder.Services.AddSingleton<ITextChunker, OverlappingTextChunker>();

// LLM
builder.Services.AddScoped<IEmbeddingService, OllamaEmbeddingService>();
builder.Services.AddScoped<ILlmProviderService, LlmProviderService>();
builder.Services.AddScoped<ICodexAuthService, CodexAuthService>();

// Projects
builder.Services.AddScoped<IProjectService, ProjectService>();
builder.Services.AddScoped<IChatClientFactory, ChatClientFactory>();

// Chapters
builder.Services.AddScoped<IChapterService, ChapterService>();
builder.Services.AddSingleton<IStaleChapterNotifier, StaleChapterNotifier>();
builder.Services.AddHostedService<StaleChapterReindexer>();

// Outline
builder.Services.AddScoped<IActService, ActService>();
builder.Services.AddScoped<IEntityService, EntityService>();
builder.Services.AddScoped<OutlineCollaborationTools>();
builder.Services.AddScoped<IOutlineCollaborationService, OutlineCollaborationService>();

// Context + AI Console
builder.Services.AddSingleton<IContextBuilder, ContextBuilder>();
builder.Services.Configure<AiConsoleOptions>(builder.Configuration.GetSection("AiConsole"));
builder.Services.AddScoped<AiConsoleTools>();
builder.Services.AddScoped<IAiConsoleService, AiConsoleService>();
builder.Services.AddScoped<IAiConsoleHistoryService, AiConsoleHistoryService>();

var app = builder.Build();

// Apply EF Core migrations + initialise sqlite-vec tables.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    var pending = db.Database.GetPendingMigrations().ToList();
    if (pending.Count > 0)
    {
        // Defensive: SQLite + single-instance dev means a stranded row in
        // __EFMigrationsLock from a previously killed/crashed migration will cause
        // Migrate() to spin forever waiting for the (non-existent) other instance.
        try
        {
            db.Database.ExecuteSqlRaw("DELETE FROM \"__EFMigrationsLock\";");
        }
        catch
        {
            // Table may not exist yet on a fresh DB; ignore.
        }
        db.Database.Migrate();
    }

    VectorStoreInitializer.Initialize(builder.Configuration, startupLogger);
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapCodexOAuth();

app.Run();
