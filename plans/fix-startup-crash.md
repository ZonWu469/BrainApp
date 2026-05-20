# Runtime Crash Fix — App.axaml.cs Startup Sequence

## Problem

The desktop app builds successfully (0 errors) but closes immediately when run in debug — no window appears.

## Root Cause Analysis

### 1. `desktop.MainWindow` set too late (race condition)

[`App.axaml.cs`](../src/BrainApp.Desktop/App.axaml.cs:28) uses `OnFrameworkInitializationCompleted` which is `async void`. The method:

1. Builds DI host and calls `await _host.StartAsync()`
2. Shows `LoadingWindow`
3. **Posts** a lambda via `Dispatcher.UIThread.Post()` at line 84 that eventually sets `desktop.MainWindow`
4. Calls `base.OnFrameworkInitializationCompleted()` at line 128 **before the posted lambda runs**

The posted lambda only runs **after** the current synchronous block completes. So when `base.OnFrameworkInitializationCompleted()` signals "initialization is complete", `desktop.MainWindow` is still `null`. If Avalonia has no `MainWindow` at this point, it exits.

### 2. `Host.CreateDefaultBuilder()` in GUI context

`Host.CreateDefaultBuilder()` is designed for console/ASP.NET apps. It registers `ConsoleLifetime` which hooks `Console.CancelKeyPress` and `AppDomain.ProcessExit`. In an Avalonia GUI app, this is unnecessary and may throw during `StartAsync()` if console services fail to initialize.

If `_host.StartAsync()` throws, the `catch` block swallows the exception, the `LoadingWindow` is never shown, `desktop.MainWindow` is never set → app exits with zero visual feedback.

### 3. No graceful degradation

If model loading fails (e.g., model file not found), the inner `try/catch` closes the `LoadingWindow` but then tries to create `MainWindow` anyway. However, the `SettingsViewModel.RefreshModelInfo()` calls `_llama.GetModelInfo()` which may throw if the model was never initialized, cascading the failure.

## Fix Plan

### Step 1: Replace `Host.CreateDefaultBuilder()` with `ServiceCollection`

```csharp
// Before (problematic):
_host = Host.CreateDefaultBuilder()
    .ConfigureAppConfiguration(...)
    .ConfigureServices(...)
    .Build();
await _host.StartAsync();

// After:
var services = new ServiceCollection();
services.AddMemoryCache();
// Register configuration manually from appsettings.json
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .Build();
services.Configure<LlamaSettings>(config.GetSection("LLama"));
// ... other config sections ...
services.AddSingleton<CacheService>();
services.AddSingleton<LlamaService>();
// ... other services ...
var provider = services.BuildServiceProvider();
```

**Why:** Removes `ConsoleLifetime`, `HostedServiceExecutor`, and other console-specific infrastructure. Gives us full control over DI container lifetime.

### Step 2: Set `MainWindow` BEFORE `base.OnFrameworkInitializationCompleted`

```csharp
// Set LoadingWindow as MainWindow immediately
var loadingWindow = new LoadingWindow();
desktop.MainWindow = loadingWindow;

// Call base AFTER MainWindow is set
base.OnFrameworkInitializationCompleted();
```

**Why:** Ensures Avalonia has a window to show when initialization completes. The LoadingWindow is visible immediately.

### Step 3: Load model AFTER base init using simple Task.Run

```csharp
// Don't use Dispatcher.UIThread.Post - load on background thread
_ = Task.Run(async () =>
{
    try
    {
        var llama = provider.GetRequiredService<LlamaService>();
        await llama.InitializeAsync();
        
        // Switch to MainWindow on UI thread
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var mainVm = new MainWindowViewModel(...);
            mainVm.Initialize();
            var mainWindow = new MainWindow(mainVm);
            desktop.MainWindow = mainWindow;
            loadingWindow.Close();
        });
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Model loading failed");
        
        // Still show MainWindow even if model fails
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var mainVm = new MainWindowViewModel(...);
            mainVm.Initialize();
            var mainWindow = new MainWindow(mainVm);
            desktop.MainWindow = mainWindow;
            loadingWindow.Close();
        });
    }
});
```

**Why:** Model loading happens on a background thread after the UI is visible. Even if loading fails, the MainWindow appears (with an error status badge).

### Step 4: Dispose DI container on shutdown

```csharp
// Store provider reference
private ServiceProvider? _provider;

// Hook application shutdown
desktop.Exit += (s, e) => { _provider?.Dispose(); };
```

## Files to Modify

| File | Change |
|------|--------|
| [`src/BrainApp.Desktop/App.axaml.cs`](../src/BrainApp.Desktop/App.axaml.cs) | Complete rewrite of startup sequence |

## Files Not Modified

| File | Reason |
|------|--------|
| [`LoadingWindow.axaml`](../src/BrainApp.Desktop/LoadingWindow.axaml) | No change needed — layout is correct |
| [`LoadingWindow.axaml.cs`](../src/BrainApp.Desktop/LoadingWindow.axaml.cs) | Has its own `InitializeComponent()` which shadows Avalonia's — this works but is unusual. No change needed. |
| [`MainWindow.axaml`](../src/BrainApp.Desktop/MainWindow.axaml) | Layout is correct |
| [`MainWindow.axaml.cs`](../src/BrainApp.Desktop/MainWindow.axaml.cs) | Constructor pattern is correct |
| [`Program.cs`](../src/BrainApp.Desktop/Program.cs) | Standard Avalonia bootstrap — no change needed |

## Verification

1. `dotnet build BrainApp.sln` — 0 errors
2. `dotnet run --project src/BrainApp.Desktop` — LoadingWindow appears immediately, model loads in background, MainWindow appears after load completes
3. If model file is missing: MainWindow appears with "Model not found" status badge
