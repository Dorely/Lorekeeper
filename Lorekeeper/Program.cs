using Lorekeeper.Auth;
using Lorekeeper.Chapters;
using Lorekeeper.Components;
using Lorekeeper.Context;
using Lorekeeper.EditorChat;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Graph;
using Lorekeeper.ChapterVisuals;
using Lorekeeper.Images;
using Lorekeeper.ImagesChat;
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
using ElectronNET.API;
using ElectronNET.API.Entities;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var isElectronMode = IsElectronMode(args);
var desktopUrl = isElectronMode ? GetDesktopUrl(builder.Configuration) : null;
var maxInteractiveServerMessageSize = builder.Configuration.GetValue<long?>("Blazor:MaximumReceiveMessageSizeBytes")
    ?? 64L * 1024 * 1024;

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options => options.MaximumReceiveMessageSize = maxInteractiveServerMessageSize);

builder.Services.AddHttpClient();

if (isElectronMode)
{
    builder.Services.AddElectron();
    builder.UseElectron(args, () => ElectronAppReady(desktopUrl!));
    builder.WebHost.UseUrls(desktopUrl!);
}

// Persistence
builder.Services.AddLorekeeperPersistence(builder.Configuration);
builder.Services.AddScoped<ILlmProviderRepository, LlmProviderRepository>();
builder.Services.AddScoped<IEmbeddingConfigurationRepository, EmbeddingConfigurationRepository>();
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
builder.Services.AddScoped<IProjectImageConversationRepository, ProjectImageConversationRepository>();
builder.Services.AddScoped<IAiChangeRepository, AiChangeRepository>();
builder.Services.AddScoped<IContestRepository, ContestRepository>();
builder.Services.AddScoped<IEditorContextPreferenceRepository, EditorContextPreferenceRepository>();
builder.Services.AddScoped<IEditorRevisionRepository, EditorRevisionRepository>();
builder.Services.AddScoped<IIngestRepository, IngestRepository>();
builder.Services.AddScoped<IWebIngestCandidateRepository, WebIngestCandidateRepository>();
builder.Services.AddScoped<IProjectImportRepository, ProjectImportRepository>();

// Knowledge
builder.Services.AddScoped<SqliteVecVectorStore>();
builder.Services.AddScoped<IVectorStore>(sp => sp.GetRequiredService<SqliteVecVectorStore>());
builder.Services.AddScoped<IVectorStoreMaintenance>(sp => sp.GetRequiredService<SqliteVecVectorStore>());
builder.Services.AddScoped<IGraphStore, RelationalGraphStore>();
builder.Services.AddScoped<IProjectGraphService, ProjectGraphService>();
builder.Services.AddSingleton<ITextChunker, OverlappingTextChunker>();

// LLM
builder.Services.Configure<EmbeddingRebuildOptions>(builder.Configuration.GetSection(EmbeddingRebuildOptions.SectionName));
builder.Services.AddSingleton<IEmbeddingRebuildQueue, EmbeddingRebuildQueue>();
builder.Services.AddScoped<IEmbeddingClient, EmbeddingClient>();
builder.Services.AddScoped<IEmbeddingConfigurationService, EmbeddingConfigurationService>();
builder.Services.AddScoped<IEmbeddingService, ProviderEmbeddingService>();
builder.Services.AddScoped<EmbeddingRebuildService>();
builder.Services.AddScoped<ILlmProviderService, LlmProviderService>();
builder.Services.AddScoped<ICodexAuthService, CodexAuthService>();
builder.Services.AddHostedService<EmbeddingRebuildWorker>();

// Search providers
builder.Services.AddScoped<IWebSearchClient, SerpApiWebSearchClient>();
builder.Services.AddScoped<IWebSearchClient, BraveWebSearchClient>();
builder.Services.AddScoped<IWebSearchProviderFactory, WebSearchProviderFactory>();
builder.Services.AddScoped<ISearchProviderService, SearchProviderService>();
builder.Services.AddScoped<IProjectSearchIndex, SqliteFtsProjectSearchIndex>();
builder.Services.AddScoped<IProjectSearchService, ProjectSearchService>();
builder.Services.AddScoped<IGraphAutoLinkService, GraphAutoLinkService>();

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
builder.Services.AddScoped<IVisionModelClientFactory, VisionModelClientFactory>();

// Chapters
builder.Services.AddScoped<IChapterService, ChapterService>();
builder.Services.AddScoped<IChapterVisualService, ChapterVisualService>();
builder.Services.AddScoped<IProjectImageService, ProjectImageService>();
builder.Services.Configure<EntityVisualContextOptions>(builder.Configuration.GetSection(EntityVisualContextOptions.SectionName));
builder.Services.AddScoped<IEntityVisualExampleService, EntityVisualExampleService>();
builder.Services.AddScoped<IEntityVisualContextService, EntityVisualContextService>();
builder.Services.Configure<ProjectImageGenerationOptions>(builder.Configuration.GetSection(ProjectImageGenerationOptions.SectionName));
builder.Services.AddScoped<IProjectImageProvider, CodexProjectImageProvider>();
builder.Services.AddScoped<IProjectImageJobService, ProjectImageJobService>();
builder.Services.AddSingleton<IProjectImageGenerationRuntime, ProjectImageGenerationRuntime>();
builder.Services.AddHostedService<ProjectImageGenerationStartupWorker>();

// Outline
builder.Services.AddScoped<IActService, ActService>();
builder.Services.AddScoped<IEntityService, EntityService>();
builder.Services.AddScoped<IEntityTypeService, EntityTypeService>();
builder.Services.AddScoped<IProjectFactService, ProjectFactService>();
builder.Services.AddScoped<IOutlineGraphSync, OutlineGraphSync>();
builder.Services.AddScoped<OutlineCollaborationTools>();
builder.Services.AddScoped<IAiChangeApprovalService, AiChangeApprovalService>();
builder.Services.AddScoped<IOutlineCollaborationService, OutlineCollaborationService>();
builder.Services.AddSingleton<IOutlineChatTurnRunner, OutlineChatTurnRunner>();

// Writing samples
builder.Services.AddScoped<IWritingSampleService, WritingSampleService>();
builder.Services.AddScoped<WritingCoachTools>();
builder.Services.AddScoped<IWritingCoachService, WritingCoachService>();
builder.Services.AddSingleton<IWritingCoachTurnRunner, WritingCoachTurnRunner>();

// Ingest
builder.Services.Configure<IngestSourceStructureOptions>(builder.Configuration.GetSection(IngestSourceStructureOptions.SectionName));
builder.Services.Configure<BookArtifactIngestOptions>(builder.Configuration.GetSection(BookArtifactIngestOptions.SectionName));
builder.Services.AddSingleton<IIngestJobQueue, IngestJobQueue>();
builder.Services.AddSingleton<IIngestJobNotifier, IngestJobNotifier>();
builder.Services.AddScoped<IIngestSourceStructureBuilder, IngestSourceStructureBuilder>();
builder.Services.AddScoped<IBookArtifactPreprocessor, BookArtifactPreprocessor>();
builder.Services.AddScoped<IIngestGraphSync, IngestGraphSync>();
builder.Services.AddScoped<IIngestGraphCleanup, IngestGraphCleanup>();
builder.Services.AddScoped<IIngestVectorIndexingService, IngestVectorIndexingService>();
builder.Services.AddScoped<IngestAgentTools>();
builder.Services.AddScoped<IngestJobProcessor>();
builder.Services.AddScoped<IIngestService, IngestService>();
builder.Services.AddHostedService<IngestJobWorker>();

// Research
builder.Services.Configure<WebResearchOptions>(builder.Configuration.GetSection(WebResearchOptions.SectionName));
builder.Services.AddSingleton<IWebFetchCoordinator, WebFetchCoordinator>();
builder.Services.AddSingleton<IWebHttpFetchClient, WebHttpFetchClient>();
builder.Services.AddSingleton<IWebLinkPolicy, WebLinkPolicy>();
builder.Services.AddSingleton<IWebRobotsPolicy, WebRobotsPolicy>();
builder.Services.AddScoped<IWebPageSourceReader, MediaWikiWebPageSourceReader>();
builder.Services.AddScoped<IWebPageReader, HttpWebPageReader>();
builder.Services.AddScoped<IWebIngestCandidateService, WebIngestCandidateService>();
builder.Services.AddScoped<ResearchTools>();
builder.Services.AddScoped<IResearchService, ResearchService>();
builder.Services.AddSingleton<IResearchChatTurnRunner, ResearchChatTurnRunner>();

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
builder.Services.AddScoped<IPublishService, PublishService>();

// Context + editor chat
builder.Services.AddScoped<ContextBuilder>();
builder.Services.AddScoped<IContextBuilder>(sp => sp.GetRequiredService<ContextBuilder>());
builder.Services.AddScoped<IEditorContextService>(sp => sp.GetRequiredService<ContextBuilder>());
builder.Services.AddScoped<IVectorIndexWorkCoordinator, VectorIndexWorkCoordinator>();
builder.Services.AddScoped<IContextIndexingService, ContextIndexingService>();
builder.Services.AddScoped<IContextRecommendationService, ContextRecommendationService>();
builder.Services.AddScoped<IEntityRelationContextService, EntityRelationContextService>();
builder.Services.AddScoped<EditorChatTools>();
builder.Services.AddScoped<IEditorContestService, EditorContestService>();
builder.Services.AddSingleton<IEditorRevisionJobNotifier, EditorRevisionJobNotifier>();
builder.Services.AddScoped<EditorRevisionAgentProcessor>();
builder.Services.AddScoped<IEditorRevisionAgentService, EditorRevisionAgentService>();
builder.Services.AddScoped<IEditorChatService, EditorChatService>();
builder.Services.AddSingleton<IEditorChatTurnRunner, EditorChatTurnRunner>();
builder.Services.AddScoped<ImagesChatTools>();
builder.Services.AddScoped<IImagesChatService, ImagesChatService>();
builder.Services.AddSingleton<IImagesChatTurnRunner, ImagesChatTurnRunner>();

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

    var embeddingConfiguration = await db.EmbeddingConfigurations.AsNoTracking().FirstOrDefaultAsync();
    var vectorMaintenance = scope.ServiceProvider.GetRequiredService<IVectorStoreMaintenance>();
    vectorMaintenance.Initialize(embeddingConfiguration?.Dimensions);

    var projectRepository = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
    var outlineGraphSync = scope.ServiceProvider.GetRequiredService<IOutlineGraphSync>();
    foreach (var project in await projectRepository.ListAsync())
        await outlineGraphSync.RepairProjectAsync(project.Id);
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    if (!isElectronMode)
    {
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
        app.UseHsts();
    }
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (!isElectronMode)
    app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<Lorekeeper.Components.App>()
    .AddInteractiveServerRenderMode();

app.MapCodexOAuth();
app.MapProjectImages();
app.MapPublishEndpoints();

app.Run();

static async Task ElectronAppReady(string desktopUrl)
{
    var options = new BrowserWindowOptions
    {
        Title = "Lorekeeper",
        Show = false,
        Width = 1440,
        Height = 960,
        MinWidth = 1024,
        MinHeight = 700,
        Center = true,
        IsRunningBlazor = true
    };

    if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        options.AutoHideMenuBar = true;

    var browserWindow = await Electron.WindowManager.CreateWindowAsync(options, desktopUrl);
    browserWindow.OnReadyToShow += () => browserWindow.Show();
}

static bool IsElectronMode(string[] args) =>
    args.Any(IsElectronArgument);

static bool IsElectronArgument(string arg)
{
    var normalized = arg.TrimStart('-', '/');
    return normalized.Equals("electron", StringComparison.OrdinalIgnoreCase)
        || normalized.StartsWith("electronPort=", StringComparison.OrdinalIgnoreCase)
        || normalized.StartsWith("electronPID=", StringComparison.OrdinalIgnoreCase)
        || normalized.StartsWith("electronAuthToken=", StringComparison.OrdinalIgnoreCase);
}

static string GetDesktopUrl(IConfiguration configuration)
{
    var bindHost = configuration["Desktop:BindHost"];
    if (string.IsNullOrWhiteSpace(bindHost))
        throw new InvalidOperationException("Desktop:BindHost must be configured to run the desktop shell.");

    if (bindHost.Contains("://", StringComparison.Ordinal))
        throw new InvalidOperationException("Desktop:BindHost must be a host name only, without a URL scheme.");

    var httpPort = configuration.GetValue<int?>("Desktop:HttpPort")
        ?? throw new InvalidOperationException("Desktop:HttpPort must be configured to run the desktop shell.");
    if (httpPort is <= 0 or > 65535)
        throw new InvalidOperationException("Desktop:HttpPort must be between 1 and 65535.");

    return $"http://{bindHost.Trim()}:{httpPort}";
}
