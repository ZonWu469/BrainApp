using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using BrainApp.Core.Config;
using BrainApp.Core.Services;
using BrainApp.Core.Skills;
using BrainApp.Desktop.ViewModels;

namespace BrainApp.Desktop;

public partial class App : Application
{
    private IServiceProvider? _serviceProvider = null!;
    private LoadingWindow? _loadingWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        // Configure Serilog first (before anything else)
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BrainApp", "logs");
        Directory.CreateDirectory(logPath);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(logPath, "brainapp-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            // Load configuration
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .Build();

            // Build DI container manually (avoids ConsoleLifetime from Host.CreateDefaultBuilder)
            var services = new ServiceCollection();
            services.AddMemoryCache();
            services.Configure<LlamaSettings>(config.GetSection("LLama"));
            services.Configure<CacheSettings>(config.GetSection("Cache"));
            services.Configure<RetrievalSettings>(config.GetSection("Retrieval"));
            services.Configure<StorageSettings>(config.GetSection("Storage"));
            services.Configure<ApiSettings>(config.GetSection("Api"));
            services.Configure<SkillsSettings>(config.GetSection("Skills"));
            services.AddSingleton<CacheService>();
            services.AddSingleton<LlamaService>();
            services.AddSingleton<RetrievalService>();
            services.AddSingleton<IngestionService>();
            services.AddSingleton<ChatService>();
            services.AddSingleton<ProfileRepository>();
            services.AddSingleton<IndexingStatusService>();
            services.AddSingleton<SkillScriptEngine>();
            services.AddSingleton<SkillExecutor>();
            services.AddSingleton<SkillFetchCache>();
            services.AddSingleton<SkillCatalogService>();
            services.AddTransient<MainWindowViewModel>();
            services.AddTransient<ChatViewModel>();
            services.AddTransient<DocumentsViewModel>();
            services.AddTransient<SkillsViewModel>();

            _serviceProvider = services.BuildServiceProvider();

            // Crash recovery: any document left in 'Indexing' status from a previous
            // run (process killed mid-ingest) gets flipped to 'Error' so the user
            // can retry it. Without this, the row's hash blocks re-upload forever.
            try
            {
                _serviceProvider.GetRequiredService<ProfileRepository>().ResetStuckDocuments();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to reset stuck Indexing documents on startup");
            }

            // Create loading window immediately
            _loadingWindow = new LoadingWindow();

            // Show loading window BEFORE calling base - this ensures a window is visible
            desktop.MainWindow = _loadingWindow;
            _loadingWindow.Show();

            // NOW call base - Avalonia will see MainWindow is set
            base.OnFrameworkInitializationCompleted();

            // Load model on a background thread (after UI is visible)
            _ = LoadModelBackgroundAsync(desktop);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application startup failed");

            // Show MainWindow even on catastrophic failure (error state)
            try
            {
                var mainVm = CreateMainViewModelWithError(ex);
                var mainWindow = new MainWindow(mainVm);
                desktop.MainWindow = mainWindow;
                desktop.MainWindow.Show();
                base.OnFrameworkInitializationCompleted();
            }
            catch
            {
                // Last resort: just exit
            }
        }
    }

    private async Task LoadModelBackgroundAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var llama = _serviceProvider!.GetRequiredService<LlamaService>();
            var settings = _serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<LlamaSettings>>();

            // Update loading window status
            Dispatcher.UIThread.Post(() =>
            {
                UpdateLoadingStatus(settings.Value);
            });

            Log.Information("Loading AI model {ModelFile}...", settings.Value.ChatModelFile);

            // Load model on background thread
            await Task.Run(async () => await llama.InitializeAsync());

            Log.Information("AI model loaded successfully");

            // Switch to MainWindow on UI thread
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var mainVm = CreateMainViewModel();
                mainVm.Initialize();

                var mainWindow = new MainWindow(mainVm);
                desktop.MainWindow = mainWindow;
                mainWindow.Show();

                // Close loading window
                _loadingWindow?.Close();
                _loadingWindow = null;
            });
        }
        catch (FileNotFoundException ex)
        {
            Log.Error(ex, "Model file not found: {File}", ex.FileName);

            // Show MainWindow with error state
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var mainVm = CreateMainViewModelWithError(ex);
                var mainWindow = new MainWindow(mainVm);
                desktop.MainWindow = mainWindow;
                mainWindow.Show();

                _loadingWindow?.Close();
                _loadingWindow = null;
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize AI model");

            // Show MainWindow with error state
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var mainVm = CreateMainViewModelWithError(ex);
                var mainWindow = new MainWindow(mainVm);
                desktop.MainWindow = mainWindow;
                mainWindow.Show();

                _loadingWindow?.Close();
                _loadingWindow = null;
            });
        }
    }

    private MainWindowViewModel CreateMainViewModel()
    {
        var llama = _serviceProvider!.GetRequiredService<LlamaService>();
        var profileRepo = _serviceProvider.GetRequiredService<ProfileRepository>();
        var retrieval = _serviceProvider.GetRequiredService<RetrievalService>();
        var settings = _serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<LlamaSettings>>();
        var storage = _serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageSettings>>();

        var vm = new MainWindowViewModel(llama, profileRepo, retrieval, settings, storage);

        // Wire up child ViewModels
        var chatVm = _serviceProvider.GetRequiredService<ChatViewModel>();
        var docsVm = _serviceProvider.GetRequiredService<DocumentsViewModel>();
        var skillsVm = _serviceProvider.GetRequiredService<SkillsViewModel>();
        vm.ChatViewModel = chatVm;
        vm.DocumentsViewModel = docsVm;
        vm.SkillsViewModel = skillsVm;

        // Update model status
        var health = llama.HealthCheck();
        vm.UpdateModelStatusFromHealth(health);

        return vm;
    }

    private MainWindowViewModel CreateMainViewModelWithError(Exception ex)
    {
        var llama = _serviceProvider!.GetRequiredService<LlamaService>();
        var profileRepo = _serviceProvider.GetRequiredService<ProfileRepository>();
        var retrieval = _serviceProvider.GetRequiredService<RetrievalService>();
        var settings = _serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<LlamaSettings>>();
        var storage = _serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageSettings>>();

        var vm = new MainWindowViewModel(llama, profileRepo, retrieval, settings, storage);

        var chatVm = _serviceProvider.GetRequiredService<ChatViewModel>();
        var docsVm = _serviceProvider.GetRequiredService<DocumentsViewModel>();
        var skillsVm = _serviceProvider.GetRequiredService<SkillsViewModel>();
        vm.ChatViewModel = chatVm;
        vm.DocumentsViewModel = docsVm;
        vm.SkillsViewModel = skillsVm;

        // Show error state
        vm.ModelStatusText = "Error: " + ex.Message.Split('\n')[0];
        vm.IsModelReady = false;

        return vm;
    }

    private void UpdateLoadingStatus(LlamaSettings settings)
    {
        if (_loadingWindow == null) return;

        var ctrl = _loadingWindow as Control;
        if (ctrl == null) return;

        var modelNameText = ctrl.FindControl<TextBlock>("ModelNameText");
        var modelSizeText = ctrl.FindControl<TextBlock>("ModelSizeText");
        var statusText = ctrl.FindControl<TextBlock>("StatusText");

        if (modelNameText != null)
            modelNameText.Text = $"Model: {settings.ChatModelFile}";

        if (modelSizeText != null)
        {
            var modelPath = Path.Combine(AppContext.BaseDirectory, settings.ModelsFolder, settings.ChatModelFile);
            if (File.Exists(modelPath))
            {
                var sizeGb = new FileInfo(modelPath).Length / (1024.0 * 1024.0 * 1024.0);
                modelSizeText.Text = $"Size: {sizeGb:F2} GB";
            }
            else
            {
                modelSizeText.Text = "Size: Not found";
            }
        }

        if (statusText != null)
            statusText.Text = $"Loading {settings.ChatModelFile}...";
    }
}