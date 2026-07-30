using Lorekeeper.Auth;
using Lorekeeper.Chapters;
using Lorekeeper.Components;
using Lorekeeper.Context;
using Lorekeeper.Desktop;
using Lorekeeper.EditorChat;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Graph;
using Lorekeeper.Fonts;
using Lorekeeper.ChapterVisuals;
using Lorekeeper.ChatTurns;
using Lorekeeper.Images;
using Lorekeeper.ImagesChat;
using Lorekeeper.ImportExport;
using Lorekeeper.Ingest;
using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Manuscripts;
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
var desktopUpdates = new DesktopUpdateService();
IDesktopReleaseUpdateChecker? desktopReleaseUpdateChecker = null;
var isElectronMode = IsElectronMode(args);
var desktopUrl = isElectronMode ? GetDesktopUrl(builder.Configuration) : null;
var enableDesktopDevTools = builder.Environment.IsDevelopment();
var desktopUpdateCheckIntervalMinutes = builder.Configuration.GetValue("Desktop:UpdateCheckIntervalMinutes", 15);
if (desktopUpdateCheckIntervalMinutes <= 0)
    throw new InvalidOperationException("Desktop:UpdateCheckIntervalMinutes must be greater than zero.");
var desktopUpdateCheckInterval = TimeSpan.FromMinutes(desktopUpdateCheckIntervalMinutes);
using var desktopUpdateMonitorCancellation = new CancellationTokenSource();
var usePerUserDataDirectory = isElectronMode
    && !builder.Environment.IsDevelopment()
    && builder.Configuration.GetValue("Desktop:UsePerUserDataDirectory", true);
var databaseConnectionString = SqliteConnectionSettings.BuildConnectionString(
    builder.Configuration,
    usePerUserDataDirectory,
    builder.Environment.ContentRootPath);
builder.Configuration["ConnectionStrings:DefaultConnection"] = databaseConnectionString;
var maxInteractiveServerMessageSize = builder.Configuration.GetValue<long?>("Blazor:MaximumReceiveMessageSizeBytes")
    ?? 64L * 1024 * 1024;

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options => options.MaximumReceiveMessageSize = maxInteractiveServerMessageSize);

builder.Services.AddHttpClient();
builder.Services.AddHttpClient<IDesktopReleaseUpdateChecker, GitHubDesktopReleaseUpdateChecker>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Lorekeeper/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-03-10");
});

if (isElectronMode)
{
    builder.Services.AddSingleton<IDesktopUpdateService>(desktopUpdates);
    builder.Services.AddElectron();
    builder.UseElectron(args, () => ElectronAppReady(
        desktopUrl!,
        enableDesktopDevTools,
        enableAutoUpdates: !builder.Environment.IsDevelopment() && OperatingSystem.IsWindows(),
        desktopUpdates,
        () => desktopReleaseUpdateChecker
            ?? throw new InvalidOperationException("Desktop release update checker is unavailable."),
        desktopUpdateCheckInterval,
        desktopUpdateMonitorCancellation.Token));
    builder.WebHost.UseUrls(desktopUrl!);
}
else
{
    builder.Services.AddSingleton<IDesktopUpdateService>(desktopUpdates);
}

// Persistence
builder.Services.AddLorekeeperPersistence(builder.Configuration);
builder.Services.AddSingleton<IProjectMutationCoordinator>(
    _ => new ProjectMutationCoordinator(databaseConnectionString));
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
builder.Services.AddScoped<ISystemPromptComposer, SystemPromptComposer>();
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

// Token counting + advisory chat limits
builder.Services.Configure<TokenCountingOptions>(builder.Configuration.GetSection(TokenCountingOptions.SectionName));
builder.Services.AddOptions<ChatTokenLimitOptions>()
    .Bind(builder.Configuration.GetSection(ChatTokenLimitOptions.SectionName))
    .Validate(options => options.DefaultMaxInputTokens > 0, "ChatTokens:DefaultMaxInputTokens must be greater than zero.")
    .Validate(
        options => options.ModelMaxInputTokens.All(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value > 0),
        "ChatTokens:ModelMaxInputTokens keys must be non-empty and values must be greater than zero.")
    .Validate(
        options => options.ModelMaxInputTokens.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            == options.ModelMaxInputTokens.Count,
        "ChatTokens:ModelMaxInputTokens cannot contain model IDs that differ only by case.")
    .ValidateOnStart();
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.Configure<EditorChatOptions>(builder.Configuration.GetSection(EditorChatOptions.SectionName));
builder.Services.AddSingleton<TiktokenTokenCounter>();
builder.Services.AddSingleton<CharEstimateTokenCounter>();
builder.Services.AddSingleton<ITokenCounter, CompositeTokenCounter>();
builder.Services.AddSingleton<ChatTokenLimitResolver>();
builder.Services.AddSingleton<ChatTurnEngine>();
builder.Services.AddSingleton<ChatTurnRuntime>();
builder.Services.AddScoped<IChatImageAttachmentService, ChatImageAttachmentService>();

// Projects
builder.Services.AddScoped<IProjectService, ProjectService>();
builder.Services.AddScoped<IBookBriefService, BookBriefService>();
builder.Services.AddScoped<IChatClientFactory, ChatClientFactory>();
builder.Services.AddScoped<IVisionModelClientFactory, VisionModelClientFactory>();

// Chapters
builder.Services.AddScoped<ChapterService>();
builder.Services.AddScoped<IChapterService>(services => services.GetRequiredService<ChapterService>());
builder.Services.AddScoped<IManuscriptService>(services => services.GetRequiredService<ChapterService>());
builder.Services.AddSingleton<IManuscriptMigrationService, ManuscriptMigrationService>();
builder.Services.AddScoped<IChapterVisualService, ChapterVisualService>();
builder.Services.AddScoped<IProjectImageService, ProjectImageService>();
builder.Services.AddScoped<IProjectFontService, ProjectFontService>();
builder.Services.AddScoped<IManuscriptStyleService, ManuscriptStyleService>();
builder.Services.Configure<EntityVisualContextOptions>(builder.Configuration.GetSection(EntityVisualContextOptions.SectionName));
builder.Services.AddScoped<IEntityVisualExampleService, EntityVisualExampleService>();
builder.Services.AddScoped<IEntityVisualContextService, EntityVisualContextService>();
builder.Services.Configure<ProjectImageGenerationOptions>(builder.Configuration.GetSection(ProjectImageGenerationOptions.SectionName));
builder.Services.AddScoped<IProjectImageProvider, CodexProjectImageProvider>();
builder.Services.AddScoped<IProjectImageJobService, ProjectImageJobService>();
builder.Services.AddScoped<IImagePromptComposer, ImagePromptComposer>();
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
builder.Services.AddScoped<IPublicationEditionService, PublicationEditionService>();
builder.Services.AddScoped<IPublicationActorContext, PublicationActorContext>();
builder.Services.AddScoped<PublishAssistantTools>();
builder.Services.AddScoped<IPublishAssistantService, PublishAssistantService>();
builder.Services.AddSingleton<IPublicationEditionMigrationService, PublicationEditionMigrationService>();
builder.Services.AddScoped<IPageGeometryService, PageGeometryService>();

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
if (isElectronMode)
    desktopReleaseUpdateChecker = app.Services.GetRequiredService<IDesktopReleaseUpdateChecker>();
app.Lifetime.ApplicationStopping.Register(desktopUpdateMonitorCancellation.Cancel);

// Apply EF Core migrations + initialise sqlite-vec tables.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    var manuscriptMigration = scope.ServiceProvider.GetRequiredService<IManuscriptMigrationService>();
    await manuscriptMigration.ApplyPendingAsync(db);
    var editionMigration = scope.ServiceProvider.GetRequiredService<IPublicationEditionMigrationService>();
    await editionMigration.ApplyPendingAsync(db);

    var embeddingConfiguration = await db.EmbeddingConfigurations.AsNoTracking().FirstOrDefaultAsync();
    var vectorMaintenance = scope.ServiceProvider.GetRequiredService<IVectorStoreMaintenance>();
    vectorMaintenance.Initialize(embeddingConfiguration?.Dimensions);

    var projectRepository = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
    var outlineGraphSync = scope.ServiceProvider.GetRequiredService<IOutlineGraphSync>();
    var chapterVisuals = scope.ServiceProvider.GetRequiredService<IChapterVisualService>();
    var repairedTextLayouts = await chapterVisuals.RepairTextLayoutsAsync();
    if (repairedTextLayouts > 0)
        startupLogger.LogInformation("Repaired {ChapterCount} chapter Picture Page text layout(s).", repairedTextLayouts);
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
app.MapProjectFonts();
app.MapPublishEndpoints();

app.Run();

static async Task ElectronAppReady(
    string desktopUrl,
    bool enableDevTools,
    bool enableAutoUpdates,
    DesktopUpdateService desktopUpdates,
    Func<IDesktopReleaseUpdateChecker> getReleaseUpdateChecker,
    TimeSpan updateCheckInterval,
    CancellationToken cancellationToken)
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
        IsRunningBlazor = true,
        WebPreferences = new WebPreferences
        {
            DevTools = enableDevTools,
            NodeIntegration = false,
            NodeIntegrationInWorker = false,
            NodeIntegrationInSubFrames = false,
            ContextIsolation = true,
            Sandbox = true,
            WebSecurity = true,
            AllowRunningInsecureContent = false,
            Plugins = false,
            ExperimentalFeatures = false,
            WebviewTag = false,
            EnableRemoteModule = false
        }
    };

    if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        options.AutoHideMenuBar = true;

    if (OperatingSystem.IsMacOS())
        Electron.App.WindowAllClosed += Electron.App.Quit;

    var browserWindow = await Electron.WindowManager.CreateWindowAsync(options, desktopUrl);
    browserWindow.OnReadyToShow += () => browserWindow.Show();

    var isPortable = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PORTABLE_EXECUTABLE_DIR"));
    if (enableAutoUpdates && !isPortable)
    {
        ConfigureElectronAutoUpdater(desktopUpdates);
        _ = MonitorElectronUpdatesAsync(desktopUpdates, updateCheckInterval, cancellationToken);
    }
    else if (!enableDevTools && (OperatingSystem.IsMacOS() || isPortable))
    {
        var currentVersion = await Electron.App.GetVersionAsync(cancellationToken);
        desktopUpdates.EnableManualDownloads(OpenReleaseInDefaultBrowserAsync);
        _ = MonitorManualUpdatesAsync(
            currentVersion,
            getReleaseUpdateChecker(),
            desktopUpdates,
            updateCheckInterval,
            cancellationToken);
    }
}

static async Task OpenReleaseInDefaultBrowserAsync(Uri releaseUri, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    var error = await Electron.Shell.OpenExternalAsync(releaseUri.AbsoluteUri);
    if (!string.IsNullOrWhiteSpace(error))
        throw new InvalidOperationException($"The default browser could not open the release page: {error}");
}

static void ConfigureElectronAutoUpdater(DesktopUpdateService desktopUpdates)
{
    Electron.AutoUpdater.AutoDownload = true;
    Electron.AutoUpdater.AutoInstallOnAppQuit = true;
    Electron.AutoUpdater.AllowPrerelease = false;
    desktopUpdates.Enable(() => Electron.AutoUpdater.QuitAndInstall(isSilent: true, isForceRunAfter: true));
    Electron.AutoUpdater.OnCheckingForUpdate += desktopUpdates.MarkChecking;
    Electron.AutoUpdater.OnUpdateAvailable += info => desktopUpdates.MarkDownloading(info.Version);
    Electron.AutoUpdater.OnUpdateNotAvailable += info => desktopUpdates.MarkIdle(info.Version);
    Electron.AutoUpdater.OnDownloadProgress += progress =>
        desktopUpdates.MarkDownloading(desktopUpdates.Snapshot.Version, progress.Percent);
    Electron.AutoUpdater.OnUpdateDownloaded += info => desktopUpdates.MarkReady(info.Version);
    Electron.AutoUpdater.OnError += error =>
    {
        desktopUpdates.MarkError(error);
        Console.Error.WriteLine($"Electron auto-update failed: {error}");
    };
}

static async Task MonitorElectronUpdatesAsync(
    DesktopUpdateService desktopUpdates,
    TimeSpan updateCheckInterval,
    CancellationToken cancellationToken)
{
    if (cancellationToken.IsCancellationRequested) return;

    await CheckForElectronUpdatesAsync(desktopUpdates);

    using var timer = new PeriodicTimer(updateCheckInterval);
    try
    {
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (desktopUpdates.Snapshot.Status is DesktopUpdateStatus.Downloading
                or DesktopUpdateStatus.Ready
                or DesktopUpdateStatus.Restarting)
            {
                continue;
            }

            await CheckForElectronUpdatesAsync(desktopUpdates);
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // Normal application shutdown.
    }
}

static async Task CheckForElectronUpdatesAsync(DesktopUpdateService desktopUpdates)
{
    try
    {
        await Electron.AutoUpdater.CheckForUpdatesAsync();
    }
    catch (Exception exception)
    {
        desktopUpdates.MarkError(exception.Message);
        Console.Error.WriteLine($"Electron auto-update check failed: {exception.Message}");
    }
}

static async Task MonitorManualUpdatesAsync(
    string currentVersion,
    IDesktopReleaseUpdateChecker releaseUpdateChecker,
    DesktopUpdateService desktopUpdates,
    TimeSpan updateCheckInterval,
    CancellationToken cancellationToken)
{
    if (cancellationToken.IsCancellationRequested) return;

    await CheckForManualUpdateAsync(currentVersion, releaseUpdateChecker, desktopUpdates, cancellationToken);

    using var timer = new PeriodicTimer(updateCheckInterval);
    try
    {
        while (await timer.WaitForNextTickAsync(cancellationToken))
            await CheckForManualUpdateAsync(currentVersion, releaseUpdateChecker, desktopUpdates, cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // Normal application shutdown.
    }
}

static async Task CheckForManualUpdateAsync(
    string currentVersion,
    IDesktopReleaseUpdateChecker releaseUpdateChecker,
    DesktopUpdateService desktopUpdates,
    CancellationToken cancellationToken)
{
    try
    {
        var update = await releaseUpdateChecker.CheckAsync(currentVersion, cancellationToken);
        if (update is null)
            desktopUpdates.MarkIdle(currentVersion);
        else
            desktopUpdates.MarkManualUpdateAvailable(update.Version, update.ReleaseUri);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception exception)
    {
        // Keep any previously discovered release visible through transient failures.
        Console.Error.WriteLine($"Manual desktop update check failed: {exception.Message}");
    }
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
