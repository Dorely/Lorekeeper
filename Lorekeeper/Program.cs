using Lorekeeper.Auth;
using Lorekeeper.Chapters;
using Lorekeeper.Components;
using Lorekeeper.Context;
using Lorekeeper.EditorChat;
using Lorekeeper.Graph;
using Lorekeeper.ImportExport;
using Lorekeeper.Ingest;
using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Projects;
using Lorekeeper.Publish;
using Lorekeeper.Research;
using Lorekeeper.Search;
using Lorekeeper.Tokens;
using Lorekeeper.Writing;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var maxInteractiveServerMessageSize = builder.Configuration.GetValue<long?>("Blazor:MaximumReceiveMessageSizeBytes")
    ?? 64L * 1024 * 1024;

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options => options.MaximumReceiveMessageSize = maxInteractiveServerMessageSize);

builder.Services.AddHttpClient();

// Persistence
builder.Services.AddLorekeeperPersistence(builder.Configuration);
builder.Services.AddScoped<ILlmProviderRepository, LlmProviderRepository>();
builder.Services.AddScoped<ISearchProviderRepository, SearchProviderRepository>();
builder.Services.AddScoped<IOAuthTokenRepository, OAuthTokenRepository>();
builder.Services.AddScoped<IProjectRepository, ProjectRepository>();
builder.Services.AddScoped<IGraphNodeRepository, GraphNodeRepository>();
builder.Services.AddScoped<IGraphEdgeRepository, GraphEdgeRepository>();
builder.Services.AddScoped<IGraphEntityTypeRepository, GraphEntityTypeRepository>();
builder.Services.AddScoped<IChapterRepository, ChapterRepository>();
builder.Services.AddScoped<IActRepository, ActRepository>();
builder.Services.AddScoped<IOutlineConversationRepository, OutlineConversationRepository>();
builder.Services.AddScoped<IEditorConversationRepository, EditorConversationRepository>();
builder.Services.AddScoped<IWritingSampleRepository, WritingSampleRepository>();
builder.Services.AddScoped<IWritingCoachConversationRepository, WritingCoachConversationRepository>();
builder.Services.AddScoped<IResearchConversationRepository, ResearchConversationRepository>();
builder.Services.AddScoped<IAiChangeRepository, AiChangeRepository>();
builder.Services.AddScoped<IContestRepository, ContestRepository>();
builder.Services.AddScoped<IEditorContextPreferenceRepository, EditorContextPreferenceRepository>();
builder.Services.AddScoped<IIngestRepository, IngestRepository>();
builder.Services.AddScoped<IWebIngestCandidateRepository, WebIngestCandidateRepository>();
builder.Services.AddScoped<IProjectImportRepository, ProjectImportRepository>();

// Knowledge
builder.Services.AddScoped<IVectorStore, SqliteVecVectorStore>();
builder.Services.AddScoped<IGraphStore, RelationalGraphStore>();
builder.Services.AddScoped<IProjectGraphService, ProjectGraphService>();
builder.Services.AddSingleton<ITextChunker, OverlappingTextChunker>();

// LLM
builder.Services.AddScoped<IEmbeddingService, OllamaEmbeddingService>();
builder.Services.AddScoped<ILlmProviderService, LlmProviderService>();
builder.Services.AddScoped<ICodexAuthService, CodexAuthService>();

// Search providers
builder.Services.AddScoped<IWebSearchClient, SerpApiWebSearchClient>();
builder.Services.AddScoped<IWebSearchClient, BraveWebSearchClient>();
builder.Services.AddScoped<IWebSearchProviderFactory, WebSearchProviderFactory>();
builder.Services.AddScoped<ISearchProviderService, SearchProviderService>();

// Token counting + prompt budgets
builder.Services.Configure<TokenCountingOptions>(builder.Configuration.GetSection(TokenCountingOptions.SectionName));
builder.Services.Configure<TokenBudgetOptions>(builder.Configuration.GetSection(TokenBudgetOptions.SectionName));
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.Configure<EditorChatOptions>(builder.Configuration.GetSection(EditorChatOptions.SectionName));
builder.Services.AddSingleton<TiktokenTokenCounter>();
builder.Services.AddSingleton<CharEstimateTokenCounter>();
builder.Services.AddSingleton<ITokenCounter, CompositeTokenCounter>();
builder.Services.AddSingleton<ITokenBudgetPlanner, TokenBudgetPlanner>();

// Projects
builder.Services.AddScoped<IProjectService, ProjectService>();
builder.Services.AddScoped<IChatClientFactory, ChatClientFactory>();

// Chapters
builder.Services.AddScoped<IChapterService, ChapterService>();

// Outline
builder.Services.AddScoped<IActService, ActService>();
builder.Services.AddScoped<IEntityService, EntityService>();
builder.Services.AddScoped<IEntityTypeService, EntityTypeService>();
builder.Services.AddScoped<IProjectFactService, ProjectFactService>();
builder.Services.AddScoped<IOutlineGraphSync, OutlineGraphSync>();
builder.Services.AddScoped<OutlineCollaborationTools>();
builder.Services.AddScoped<IAiChangeApprovalService, AiChangeApprovalService>();
builder.Services.AddScoped<IOutlineCollaborationService, OutlineCollaborationService>();

// Writing samples
builder.Services.AddScoped<IWritingSampleService, WritingSampleService>();
builder.Services.AddScoped<WritingCoachTools>();
builder.Services.AddScoped<IWritingCoachService, WritingCoachService>();

// Ingest
builder.Services.Configure<IngestSourceStructureOptions>(builder.Configuration.GetSection(IngestSourceStructureOptions.SectionName));
builder.Services.AddSingleton<IIngestJobQueue, IngestJobQueue>();
builder.Services.AddSingleton<IIngestJobNotifier, IngestJobNotifier>();
builder.Services.AddScoped<IIngestSourceStructureBuilder, IngestSourceStructureBuilder>();
builder.Services.AddScoped<IIngestGraphSync, IngestGraphSync>();
builder.Services.AddScoped<IIngestGraphCleanup, IngestGraphCleanup>();
builder.Services.AddScoped<IngestAgentTools>();
builder.Services.AddScoped<IngestJobProcessor>();
builder.Services.AddScoped<IIngestService, IngestService>();
builder.Services.AddHostedService<IngestJobWorker>();

// Research
builder.Services.Configure<WebResearchOptions>(builder.Configuration.GetSection(WebResearchOptions.SectionName));
builder.Services.AddScoped<IWebPageReader, HttpWebPageReader>();
builder.Services.AddScoped<IWebIngestCandidateService, WebIngestCandidateService>();
builder.Services.AddScoped<ResearchTools>();
builder.Services.AddScoped<IResearchService, ResearchService>();

// Import / export
builder.Services.AddSingleton<IProjectImportJobQueue, ProjectImportJobQueue>();
builder.Services.AddSingleton<IProjectImportJobNotifier, ProjectImportJobNotifier>();
builder.Services.AddScoped<IProjectImportExportService, ProjectImportExportService>();
builder.Services.AddScoped<ProjectImportJobProcessor>();
builder.Services.AddHostedService<ProjectImportJobWorker>();

// Publish
builder.Services.AddScoped<IPublishExportFormatter, PlainTextPublishFormatter>();
builder.Services.AddScoped<IPublishExportFormatter, MarkdownPublishFormatter>();
builder.Services.AddScoped<IPublishExportFormatter, EpubPublishFormatter>();
builder.Services.AddScoped<ICodexImageGenerationService, CodexImageGenerationService>();
builder.Services.AddScoped<IPublishService, PublishService>();

// Context + editor chat
builder.Services.AddScoped<ContextBuilder>();
builder.Services.AddScoped<IContextBuilder>(sp => sp.GetRequiredService<ContextBuilder>());
builder.Services.AddScoped<IEditorContextService>(sp => sp.GetRequiredService<ContextBuilder>());
builder.Services.AddScoped<IContextIndexingService, ContextIndexingService>();
builder.Services.AddScoped<IContextRecommendationService, ContextRecommendationService>();
builder.Services.AddScoped<IEntityRelationContextService, EntityRelationContextService>();
builder.Services.AddScoped<EditorChatTools>();
builder.Services.AddScoped<IEditorContestService, EditorContestService>();
builder.Services.AddScoped<IEditorChatService, EditorChatService>();

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

    var projectRepository = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
    var outlineGraphSync = scope.ServiceProvider.GetRequiredService<IOutlineGraphSync>();
    foreach (var project in await projectRepository.ListAsync())
        await outlineGraphSync.RepairProjectAsync(project.Id);
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
