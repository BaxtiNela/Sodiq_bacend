using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.AspNetCore.SignalR.Client;
using WA.Agent.Services;
using WA.App.Controls;
using WA.App.Services;

namespace WA.App.Views;

public partial class MainWindow : Window
{
    private readonly ToolExecutor _executor;
    private readonly MemoryService _memory;
    private readonly LocalChatStore _chatStore;
    private readonly ApiKeyStore _apiKeyStore;
    private readonly ExternalApiClient _extClient = new();

    private HubConnection? _hub;
    private string _sessionId = GenerateSessionId();
    private string _currentModel = "qwen2.5:7b";
    private bool _isBusy;
    private bool _isAutoMode;
    private string? _currentEditorPath;
    private CancellationTokenSource? _extApiCts;

    private ChatBubble? _streamBubble;
    private readonly System.Text.StringBuilder _streamBuffer = new();

    // Fayl daraxti tanlangan yo'l (context uchun)
    private string? _selectedTreePath;

    // Sessiya cache: id → xabarlar ro'yxati (tez almashtirish uchun)
    private readonly Dictionary<string, List<(MessageRole Role, string Text, string? Model)>> _sessionCache = new();

    private const string BackendUrl = "http://localhost:5000";
    private static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsAssistant");

    // ═══════════════════════════════════════════
    // INIT
    // ═══════════════════════════════════════════

    public MainWindow()
    {
        InitializeComponent();
        _memory      = new MemoryService(DataDir);
        _executor    = new ToolExecutor(_memory);
        _chatStore   = new LocalChatStore(DataDir);
        _apiKeyStore = new ApiKeyStore(DataDir);
        Loaded  += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { InitMonacoEditor(); }
        catch (Exception ex)
        {
            AddMessage(MessageRole.System, $"⚠ Monaco editor yuklanmadi: {ex.Message}\nWebView2 Runtime o'rnatilganligini tekshiring.");
        }

        try { RefreshFileTree(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)); }
        catch { }

        // Oxirgi sessionni tiklash
        RestoreLastSession();
        // Birinchi sessiya tabini qo'shish (agar yo'q bo'lsa)
        if (SessionTabs.Children.Count == 0)
            AddSessionTab(_sessionId, "Sessiya 1", activate: false);
        UpdateSessionTabsUI();

        await ConnectToBackendAsync();

        var timer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) => MemUsage.Text = $"{GC.GetTotalMemory(false) / 1024 / 1024} MB";
        timer.Start();
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_hub != null) await _hub.DisposeAsync();
    }

    // ═══════════════════════════════════════════
    // SIGNALR
    // ═══════════════════════════════════════════

    private async Task ConnectToBackendAsync()
    {
        try
        {
            _hub = new HubConnectionBuilder()
                .WithUrl($"{BackendUrl}/hub/assistant")
                .WithAutomaticReconnect()
                .Build();

            _hub.ServerTimeout  = TimeSpan.FromMinutes(10);
            _hub.KeepAliveInterval = TimeSpan.FromSeconds(15);

            _hub.On<string>("StatusUpdate", status =>
                Dispatcher.InvokeAsync(() =>
                {
                    SetStatus(status);
                    AiPanelStatus.Text = status;
                }));

            _hub.On<string, string, string>("ToolCallRequest", async (callId, toolName, argsJson) =>
            {
                Dictionary<string, object?> argsDict;
                try { argsDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson) ?? []; }
                catch { argsDict = []; }

                // 1. Activity bubble qo'shish (ishlayotgan holat)
                ToolActivityBubble? actBubble = null;
                await Dispatcher.InvokeAsync(() =>
                {
                    var preview = ToolActivityBubble.FormatArgsPreview(toolName, argsDict);
                    actBubble = new ToolActivityBubble(toolName, preview);
                    MessagesPanel.Children.Add(actBubble);
                    ChatScroll.ScrollToEnd();
                    // Activity bar yangilash
                    ActivityText.Text = $"{toolName}  {preview}";
                });

                // 2. write_file uchun ruxsat so'rash (FAQAT Ask rejimida)
                if (toolName == "write_file" && !_isAutoMode)
                {
                    string Get(string k) => argsDict.TryGetValue(k, out var v) ? v?.ToString() ?? "" : "";
                    var filePath    = Get("path");
                    var fileContent = Get("content");

                    EditPermissionBubble? permBubble = null;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        permBubble = new EditPermissionBubble(filePath, fileContent);
                        MessagesPanel.Children.Add(permBubble);
                        ChatScroll.ScrollToEnd();
                    });

                    var allowed = await permBubble!.Decision;
                    if (!allowed)
                    {
                        actBubble?.SetDone("Foydalanuvchi rad etdi", success: false);
                        if (_hub?.State == HubConnectionState.Connected)
                            await _hub.InvokeAsync("SendToolResult", _sessionId, callId, toolName,
                                "Foydalanuvchi fayl tahrirlashni rad etdi");
                        return;
                    }
                }

                // 3. Tool bajarish
                var call   = new WA.Agent.Models.ToolCall(callId, toolName, argsDict);
                var result = await _executor.ExecuteAsync(call);
                var ok     = !result.StartsWith("Xato") && !result.StartsWith("CMD xato") && !result.StartsWith("PowerShell xato");

                actBubble?.SetDone(result, ok);

                // 4. write_file muvaffaqiyatli bo'lsa — editorda yangilash + qatorlarni belgilash
                if (toolName == "write_file" && ok)
                {
                    string Get2(string k) => argsDict.TryGetValue(k, out var v) ? v?.ToString() ?? "" : "";
                    var writtenPath    = Get2("path");
                    var writtenContent = Get2("content");
                    await Dispatcher.InvokeAsync(async () =>
                        await RefreshEditorAfterWrite(writtenPath, writtenContent));
                }

                if (_hub?.State == HubConnectionState.Connected)
                    await _hub.InvokeAsync("SendToolResult", _sessionId, callId, toolName, result);
            });

            _hub.On<string>("StreamToken", token =>
                Dispatcher.InvokeAsync(() => AppendStreamToken(token)));

            _hub.On<string, string>("FinalResponse", (content, _) =>
                Dispatcher.InvokeAsync(() => OnFinalResponse(content)));

            _hub.On<string>("Error", err =>
                Dispatcher.InvokeAsync(() =>
                {
                    AddMessage(MessageRole.System, $"❌  {err}");
                    SetBusy(false);
                }));

            _hub.Reconnecting += _ =>
            {
                Dispatcher.InvokeAsync(() => SetStatus("Qayta ulanmoqda..."));
                return Task.CompletedTask;
            };
            _hub.Reconnected += _ =>
            {
                Dispatcher.InvokeAsync(() => SetStatus("Tayyor"));
                return Task.CompletedTask;
            };

            await _hub.StartAsync();
            await LoadModels();
            SetStatus("Tayyor");
            AiPanelStatus.Text = "AI Agent";
            AddMessage(MessageRole.Assistant,
                "Salom! Har qanday buyruq bering — o'zim bajaraman.\n" +
                "Papka tashlash: fayl daraxti chapda ko'rsatiladi.\n" +
                "Fayl ustiga ikki marta bosing — redaktorda ochiladi.", _currentModel);
        }
        catch (Exception ex)
        {
            AddMessage(MessageRole.System,
                $"⚠  Backend topilmadi. Docker'ni ishga tushiring:\n\n" +
                $"  cd \"C:/Users/Сomp X/dev-studio\"\n  docker compose up -d\n\n{ex.Message}");
            SetStatus("Backend o'chiq");
        }
    }

    private async Task LoadModels()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var json = await http.GetStringAsync("http://localhost:11434/api/tags");
            using var doc = JsonDocument.Parse(json);
            var models = doc.RootElement.GetProperty("models")
                .EnumerateArray()
                .Select(m => m.GetProperty("name").GetString() ?? "")
                .Where(m => !string.IsNullOrEmpty(m))
                .ToList();

            await Dispatcher.InvokeAsync(() =>
            {
                ModelCombo.Items.Clear();
                // Ollama local models
                if (models.Count == 0) ModelCombo.Items.Add("qwen2.5:7b");
                else foreach (var m in models) ModelCombo.Items.Add(m);
                // External (paid API)
                ModelCombo.Items.Add("deepseek-chat");
                ModelCombo.Items.Add("gpt-4o");

                ModelCombo.SelectedItem = ModelCombo.Items.Contains(_currentModel)
                    ? _currentModel
                    : ModelCombo.Items[0];

                if (ModelCombo.SelectedItem is string sel) _currentModel = sel;
            });
        }
        catch
        {
            ModelCombo.Items.Clear();
            ModelCombo.Items.Add("qwen2.5:7b");
            ModelCombo.SelectedIndex = 0;
        }
    }

    // ═══════════════════════════════════════════
    // SEND / RECEIVE
    // ═══════════════════════════════════════════

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendMessage();

    private async void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await SendMessage();
        }
    }

    private async Task SendMessage()
    {
        var text = ChatInput.Text.Trim();
        if (string.IsNullOrEmpty(text) || _isBusy) return;

        ChatInput.Clear();
        AddMessage(MessageRole.User, text);
        SetBusy(true);
        _streamBuffer.Clear();
        _streamBubble = null;

        // Fayl/papka kontekstini qo'shish (UI da ko'rinmaydi, backendga boradi)
        var backendText = BuildMessageWithContext(text);

        // Tashqi API (DeepSeek / OpenAI)
        if (ExternalApiClient.IsExternalModel(_currentModel))
        {
            await SendViaExternalApiAsync(backendText);
            return;
        }

        // SignalR / Ollama
        if (_hub?.State != HubConnectionState.Connected)
        {
            AddMessage(MessageRole.System, "Backend bilan aloqa yo'q. docker compose up -d ni ishga tushiring.");
            SetBusy(false);
            return;
        }

        try
        {
            await _hub.InvokeAsync("SendMessage", _sessionId, backendText, _currentModel);
        }
        catch (Exception ex)
        {
            AddMessage(MessageRole.System, $"Xato: {ex.Message}");
            SetBusy(false);
        }
    }

    private string BuildMessageWithContext(string userText)
    {
        if (string.IsNullOrEmpty(_selectedTreePath)) return userText;

        string ctx;
        if (File.Exists(_selectedTreePath))
        {
            try
            {
                var ext     = Path.GetExtension(_selectedTreePath).TrimStart('.');
                var content = File.ReadAllText(_selectedTreePath);
                if (content.Length > 12000) content = content[..12000] + "\n... (qisqartirildi)";
                ctx = $"[Fayl: {_selectedTreePath}]\n```{ext}\n{content}\n```\n\n";
            }
            catch { ctx = $"[Fayl: {_selectedTreePath}]\n\n"; }
        }
        else if (Directory.Exists(_selectedTreePath))
        {
            ctx = BuildProjectContext(_selectedTreePath);
        }
        else return userText;

        return ctx + userText;
    }

    // Loyiha kontekstini yaratish: dir tree + asosiy fayllar
    private static string BuildProjectContext(string root)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[Loyiha papkasi: {root}]");
        sb.AppendLine();
        sb.AppendLine("## Tuzilishi:");
        sb.Append(BuildDirTree(root, 3));
        sb.AppendLine();

        // Asosiy loyiha fayllarini o'qish
        var keyPatterns = new[]
        {
            "README.md", "README.txt", "package.json", "*.csproj", "*.sln",
            "pom.xml", "build.gradle", "Cargo.toml", "go.mod", "pyproject.toml",
            "requirements.txt", "Makefile", "docker-compose.yml", ".env.example",
            "tsconfig.json", "vite.config.*", "webpack.config.*"
        };

        sb.AppendLine("## Asosiy fayllar:");
        var added = 0;
        foreach (var pattern in keyPatterns)
        {
            var files = pattern.Contains('*')
                ? Directory.GetFiles(root, pattern, SearchOption.TopDirectoryOnly)
                : File.Exists(Path.Combine(root, pattern))
                    ? [Path.Combine(root, pattern)]
                    : [];

            foreach (var f in files.Take(2))
            {
                try
                {
                    var content = File.ReadAllText(f);
                    if (content.Length > 3000) content = content[..3000] + "\n...";
                    var ext = Path.GetExtension(f).TrimStart('.');
                    sb.AppendLine($"\n### {Path.GetFileName(f)}");
                    sb.AppendLine($"```{ext}");
                    sb.AppendLine(content);
                    sb.AppendLine("```");
                    if (++added >= 5) goto done;
                }
                catch { }
            }
        }
        done:
        sb.AppendLine();
        return sb.ToString();
    }

    private async Task SendViaExternalApiAsync(string userText)
    {
        var provider = ExternalApiClient.ProviderLabel(_currentModel).ToLower();
        var apiKey   = _apiKeyStore.Get(provider);

        if (string.IsNullOrEmpty(apiKey))
        {
            AddMessage(MessageRole.System,
                $"⚠ {ExternalApiClient.ProviderLabel(_currentModel)} API kaliti kiritilmagan.\n" +
                "Model yonidagi 🔑 belgini bosib, kalitni kiriting.");
            ShowApiKeyPopup();
            SetBusy(false);
            return;
        }

        // Sessiya tarixini yuklash
        var history = new List<(string Role, string Content)>();
        foreach (var child in MessagesPanel.Children.OfType<ChatBubble>())
        {
            if (child.CachedRole is MessageRole.User or MessageRole.Assistant)
                history.Add((
                    child.CachedRole == MessageRole.User ? "user" : "assistant",
                    child.CachedText ?? ""));
        }

        _extApiCts = new CancellationTokenSource();
        SetStatus($"{ExternalApiClient.ProviderLabel(_currentModel)} ishlayapti...");

        try
        {
            await _extClient.StreamAsync(
                _currentModel, apiKey, history,
                token => Dispatcher.InvokeAsync(() => AppendStreamToken(token)),
                _extApiCts.Token);

            var fullText = _streamBuffer.ToString();
            _chatStore.SaveMessage(_sessionId, "assistant", fullText, _currentModel);

            // Bilimlarni backendga o'tkazish (pulli AI → local model uchun xotira)
            _ = Task.Run(() => SaveExternalLearningToBackendAsync(userText, fullText));
        }
        catch (OperationCanceledException)
        {
            AddMessage(MessageRole.System, "Bekor qilindi");
        }
        catch (Exception ex)
        {
            AddMessage(MessageRole.System, $"❌ {ExternalApiClient.ProviderLabel(_currentModel)} xato: {ex.Message}");
        }
        finally
        {
            _streamBubble = null;
            _streamBuffer.Clear();
            _extApiCts = null;
            SetBusy(false);
            SetStatus("Tayyor");
            AiPanelStatus.Text = "AI Agent";
        }
    }

    // Pulli AI javobini backendga o'tkazish (local modellar foydalanishi uchun)
    private async Task SaveExternalLearningToBackendAsync(string userMsg, string aiResponse)
    {
        try
        {
            if (_hub?.State == HubConnectionState.Connected)
                await _hub.InvokeAsync("LearnFromExternal", userMsg, aiResponse,
                    ExternalApiClient.ProviderLabel(_currentModel));
        }
        catch { /* hub ulanmagan bo'lsa o'tkazib yuborish */ }
    }

    private void AppendStreamToken(string token)
    {
        _streamBuffer.Append(token);
        if (_streamBubble == null)
        {
            _streamBubble = new ChatBubble(MessageRole.Assistant, token, _currentModel);
            MessagesPanel.Children.Add(_streamBubble);
        }
        else
        {
            _streamBubble.AppendText(token);
        }
        ChatScroll.ScrollToEnd();
    }

    private void OnFinalResponse(string content)
    {
        if (_streamBubble == null)
            AddMessage(MessageRole.Assistant, content, _currentModel);
        _streamBubble = null;
        _streamBuffer.Clear();
        SetBusy(false);
        SetStatus("Tayyor");
        AiPanelStatus.Text = "AI Agent";
        ChatScroll.ScrollToEnd();
    }

    // ═══════════════════════════════════════════
    // MONACO EDITOR
    // ═══════════════════════════════════════════

    private void InitMonacoEditor()
    {
        // WebView2 Kirill/non-ASCII yo'llarda ishlamaydi — ASCII papkaga ko'chiramiz
        var safeDir = @"C:\ProgramData\WindowsAssistant";
        Directory.CreateDirectory(safeDir);

        // UserDataFolder — ASCII yo'l (Chromium ICU bug fix)
        MonacoEditor.CreationProperties = new Microsoft.Web.WebView2.Wpf.CoreWebView2CreationProperties
        {
            UserDataFolder = Path.Combine(safeDir, "WebView2")
        };

        // HTML faylni ASCII yo'lga nusxalaymiz
        var srcHtml = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "monaco.html");
        var dstHtml = Path.Combine(safeDir, "monaco.html");
        if (File.Exists(srcHtml))
            File.Copy(srcHtml, dstHtml, overwrite: true);

        MonacoEditor.Source = File.Exists(dstHtml)
            ? new Uri(dstHtml)
            : new Uri("about:blank");
    }

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe","dll","pdb","obj","lib","a","so","dylib",
        "png","jpg","jpeg","gif","bmp","ico","tiff","webp","svg",
        "mp3","mp4","wav","ogg","flac","avi","mkv","mov",
        "zip","rar","7z","tar","gz","bz2",
        "pdf","doc","docx","xls","xlsx","ppt","pptx",
        "db","sqlite","sqlite3","mdb","accdb",
        "bin","dat","pak","iso","img",
    };

    private static string DetectLanguage(string ext) => ext switch
    {
        // Web
        "js" or "mjs" or "cjs"       => "javascript",
        "jsx"                         => "javascript",
        "ts" or "mts" or "cts"       => "typescript",
        "tsx"                         => "typescript",
        "html" or "htm" or "xhtml"   => "html",
        "vue" or "svelte"            => "html",
        "css"                        => "css",
        "scss" or "sass"             => "scss",
        "less"                       => "less",
        // .NET / JVM
        "cs" or "csx"               => "csharp",
        "vb"                        => "vb",
        "fs" or "fsx" or "fsi"      => "fsharp",
        "java"                      => "java",
        "kt" or "kts"               => "kotlin",
        "groovy" or "gradle"        => "groovy",
        "scala"                     => "scala",
        // Systems
        "c"                         => "c",
        "h"                         => "c",
        "cpp" or "cc" or "cxx"      => "cpp",
        "hpp" or "hxx"              => "cpp",
        "rs"                        => "rust",
        "go"                        => "go",
        // Scripting
        "py" or "pyw" or "pyi"      => "python",
        "rb" or "rake" or "gemspec" => "ruby",
        "php" or "phtml"            => "php",
        "lua"                       => "lua",
        "pl" or "pm"               => "perl",
        "r"                        => "r",
        "swift"                    => "swift",
        "dart"                     => "dart",
        // Shell
        "sh" or "bash" or "zsh"    => "shell",
        "fish"                     => "shell",
        "ps1" or "psm1" or "psd1"  => "powershell",
        "bat" or "cmd"             => "bat",
        // Data / Config
        "json" or "jsonc"          => "json",
        "xml" or "xaml" or "csproj" or "props" or "targets" or "resx" => "xml",
        "svg"                      => "xml",
        "yaml" or "yml"            => "yaml",
        "toml" or "ini" or "cfg" or "conf" or "env" => "ini",
        "properties"               => "ini",
        // Query
        "sql" or "mysql" or "pgsql" => "sql",
        // Docs
        "md" or "markdown" or "mdx" => "markdown",
        "txt" or "log" or "csv" or "tsv" => "plaintext",
        // Build / DevOps
        "dockerfile" or "containerfile" => "dockerfile",
        "makefile" or "mk"         => "makefile",
        // Other
        "graphql" or "gql"         => "graphql",
        "ex" or "exs"              => "elixir",
        "erl" or "hrl"             => "erlang",
        "clj" or "cljs"            => "clojure",
        "hs" or "lhs"              => "haskell",
        _                          => "plaintext"
    };

    private async void OpenFileInEditor(string path)
    {
        if (!File.Exists(path)) return;

        var ext = Path.GetExtension(path).TrimStart('.').ToLower();

        if (BinaryExtensions.Contains(ext))
        {
            AddMessage(MessageRole.System, $"⚠ Binary fayl ochib bo'lmaydi: {Path.GetFileName(path)}");
            return;
        }

        try
        {
            // Ensure WebView2 is ready
            if (MonacoEditor.CoreWebView2 == null)
                await MonacoEditor.EnsureCoreWebView2Async();

            var content = await File.ReadAllTextAsync(path);
            var lang = DetectLanguage(ext);

            // Special case: files without extension (Makefile, Dockerfile, etc.)
            if (string.IsNullOrEmpty(ext))
            {
                var name = Path.GetFileName(path).ToLower();
                lang = name switch
                {
                    "dockerfile" or "containerfile" => "dockerfile",
                    "makefile" or "gnumakefile"     => "makefile",
                    "jenkinsfile"                   => "groovy",
                    _                               => "plaintext"
                };
            }

            var payload = JsonSerializer.Serialize(new
            {
                content,
                language = lang,
                filename = Path.GetFileName(path),
                path
            });

            await MonacoEditor.CoreWebView2.ExecuteScriptAsync($"openFile({payload})");
            _currentEditorPath = path;
            AddEditorTab(path);
        }
        catch (Exception ex)
        {
            AddMessage(MessageRole.System, $"Fayl ocholmadi: {ex.Message}");
        }
    }

    // write_file dan keyin editor ni yangilash + o'zgargan qatorlarni belgilash
    private async Task RefreshEditorAfterWrite(string path, string newContent)
    {
        if (MonacoEditor.CoreWebView2 == null)
            await MonacoEditor.EnsureCoreWebView2Async();

        string oldContent = "";
        if (_currentEditorPath == path)
        {
            try
            {
                var jsResult = await MonacoEditor.CoreWebView2!.ExecuteScriptAsync("window.getContent()");
                oldContent = JsonSerializer.Deserialize<string>(jsResult) ?? "";
            }
            catch { }
        }

        var ext  = Path.GetExtension(path).TrimStart('.').ToLower();
        var lang = DetectLanguage(ext);
        var payload = JsonSerializer.Serialize(new { content = newContent, language = lang,
                                                      filename = Path.GetFileName(path), path });
        await MonacoEditor.CoreWebView2!.ExecuteScriptAsync($"openFile({payload})");
        _currentEditorPath = path;
        AddEditorTab(path);    // no-op if already open

        var changedLines = GetChangedLines(oldContent, newContent);
        if (changedLines.Count > 0)
        {
            var linesJson = JsonSerializer.Serialize(changedLines.Take(300).ToArray());
            await MonacoEditor.CoreWebView2.ExecuteScriptAsync($"highlightLines({linesJson})");
        }
    }

    private static List<int> GetChangedLines(string oldContent, string newContent)
    {
        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');
        var changed  = new List<int>();
        for (int i = 0; i < newLines.Length; i++)
        {
            var old = i < oldLines.Length ? oldLines[i] : null;
            if (old != newLines[i])
                changed.Add(i + 1);
        }
        return changed;
    }

    private void AddEditorTab(string path)
    {
        var name = Path.GetFileName(path);
        // Takrorlanishni tekshirish
        foreach (var existing in EditorTabs.Children.OfType<Border>())
            if (existing.Tag?.ToString() == path) return;

        var tab = new Border
        {
            Tag = path,
            Height = 35,
            Background = FindResource("Brush.BG.Active") as Brush,
            BorderBrush = FindResource("Brush.Accent.Blue") as Brush,
            BorderThickness = new Thickness(0, 0, 0, 2),
            Margin = new Thickness(0, 0, 1, 0),
            Cursor = Cursors.Hand
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var label = new TextBlock
        {
            Text = $"  {name}  ",
            Foreground = FindResource("Brush.Text.Primary") as Brush,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand
        };
        label.MouseLeftButtonUp += (_, _) => OpenFileInEditor(path);

        var closeBtn = new Button
        {
            Content = "✕",
            Style   = FindResource("Btn.Ghost") as Style,
            Width   = 20, Height = 20,
            FontSize = 9,
            Padding = new Thickness(0),
            Margin  = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = FindResource("Brush.Text.Muted") as Brush
        };
        closeBtn.Click += (_, _) => CloseEditorTab(tab);

        row.Children.Add(label);
        row.Children.Add(closeBtn);
        tab.Child = row;
        EditorTabs.Children.Add(tab);
        ShowEditorPanel();
    }

    private void CloseEditorTab(Border tab)
    {
        EditorTabs.Children.Remove(tab);
        if (EditorTabs.Children.Count == 0)
            HideEditorPanel();
        else
        {
            // Oxirgi ochiq tabni ko'rsat
            var last = EditorTabs.Children.OfType<Border>().LastOrDefault();
            if (last?.Tag is string p) OpenFileInEditor(p);
        }
    }

    private void ShowEditorPanel()
    {
        if (EditorCol.Width.Value < 10)
        {
            EditorCol.Width = new GridLength(520, GridUnitType.Pixel);
            EditorSplitterCol.Width = new GridLength(4);
            EditorSplitter.Visibility = Visibility.Visible;
            EditorHint.Visibility = Visibility.Collapsed;
        }
    }

    private void HideEditorPanel()
    {
        EditorCol.Width = new GridLength(0);
        EditorSplitterCol.Width = new GridLength(0);
        EditorSplitter.Visibility = Visibility.Collapsed;
        EditorTabs.Children.Clear();
    }

    // ═══════════════════════════════════════════
    // FILE TREE
    // ═══════════════════════════════════════════

    private void RefreshFileTree(string rootPath)
    {
        FileTree.Items.Clear();
        try
        {
            var root = CreateTreeNode(rootPath, expand: true);
            FileTree.Items.Add(root);
        }
        catch { }
    }

    private TreeViewItem CreateTreeNode(string path, bool expand = false)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) name = path;

        var isDir = Directory.Exists(path);
        var item = new TreeViewItem
        {
            Header     = (isDir ? "📁 " : "  📄 ") + name,
            Foreground = FindResource("Brush.Text.Secondary") as Brush,
            FontSize   = 12,
            Tag        = path
        };

        if (isDir)
        {
            item.Items.Add("...");
            item.Expanded += (s, _) => ExpandTreeNode((TreeViewItem)s);
        }

        if (expand) item.IsExpanded = true;
        return item;
    }

    private void ExpandTreeNode(TreeViewItem item)
    {
        if (item.Items.Count == 1 && item.Items[0] is string)
        {
            item.Items.Clear();
            var path = item.Tag?.ToString() ?? "";
            try
            {
                foreach (var dir  in Directory.GetDirectories(path).Take(100).OrderBy(d => d))
                    item.Items.Add(CreateTreeNode(dir));
                foreach (var file in Directory.GetFiles(path).Take(200).OrderBy(f => f))
                    item.Items.Add(CreateTreeNode(file));
            }
            catch { }
        }
    }

    private void FileTree_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileTree.SelectedItem is TreeViewItem { Tag: string path } && File.Exists(path))
            OpenFileInEditor(path);
    }

    // Fayl/papka tanlanganda context chipni ko'rsat
    private void FileTree_SelectedItemChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: string path })
        {
            _selectedTreePath     = path;
            var label             = Path.GetFileName(path);
            ContextChipLabel.Text = string.IsNullOrEmpty(label) ? path : label;
            ContextChip.Visibility = Visibility.Visible;
            // Faqat papka uchun "Taxlil" tugmasini ko'rsat
            AnalyzeBtn.Visibility = Directory.Exists(path)
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ClearContext_Click(object sender, RoutedEventArgs e)
    {
        _selectedTreePath      = null;
        ContextChip.Visibility = Visibility.Collapsed;
        AnalyzeBtn.Visibility  = Visibility.Collapsed;
    }

    // Loyihani avtomatik taxlil qilish
    private async void AnalyzeProject_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedTreePath) || !Directory.Exists(_selectedTreePath)) return;
        ChatInput.Text =
            "Bu loyihani to'liq o'rgan: arxitektura, texnologiyalar, asosiy modullar, " +
            "fayl tuzilishi va kod uslubini tahlil qil. " +
            "Keyin keyingi buyruqlarda shu loyiha bilan ishlashga tayyor tur.";
        await SendMessage();
    }

    // Papka tuzilishini matn sifatida qurish (2 darajali)
    private static string BuildDirTree(string root, int depth)
    {
        var sb = new System.Text.StringBuilder();
        BuildDirTreeRec(root, "", depth, sb);
        return sb.ToString();
    }
    private static void BuildDirTreeRec(string path, string indent, int depth,
        System.Text.StringBuilder sb)
    {
        if (depth < 0) return;
        try
        {
            foreach (var d in Directory.GetDirectories(path).Take(30).OrderBy(x => x))
            {
                sb.AppendLine(indent + "📁 " + Path.GetFileName(d));
                BuildDirTreeRec(d, indent + "  ", depth - 1, sb);
            }
            foreach (var f in Directory.GetFiles(path).Take(50).OrderBy(x => x))
                sb.AppendLine(indent + "  " + Path.GetFileName(f));
        }
        catch { }
    }

    private void FileTree_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            LoadDroppedPath(paths[0]);
    }

    // ═══════════════════════════════════════════
    // DRAG & DROP (window-level)
    // ═══════════════════════════════════════════

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            LoadDroppedPath(paths[0]);
    }

    private void LoadDroppedPath(string path)
    {
        if (Directory.Exists(path))
            RefreshFileTree(path);
        else if (File.Exists(path))
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null) RefreshFileTree(dir);
            OpenFileInEditor(path);
        }
    }

    // ═══════════════════════════════════════════
    // TOOLBAR / ACTIONS
    // ═══════════════════════════════════════════

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Papka tanlang" };
        if (dlg.ShowDialog() == true)
            RefreshFileTree(dlg.FolderName);
    }

    private void NewChat_Click(object sender, RoutedEventArgs e) => NewSession_Click(sender, e);

    private void NewSession_Click(object sender, RoutedEventArgs e)
    {
        // Joriy xabarlarni cache ga saqlash
        SaveCurrentToCache();

        // Yangi sessiya
        var newId = GenerateSessionId();
        var num   = SessionTabs.Children.Count + 1;
        AddSessionTab(newId, $"Sessiya {num}", activate: true);
    }

    private void ClearChat_Click(object sender, RoutedEventArgs e) =>
        MessagesPanel.Children.Clear();

    private void AttachFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = false };
        if (dlg.ShowDialog() == true)
            ChatInput.AppendText($"\n[Fayl: {dlg.FileName}]");
    }

    private void Voice_Click(object sender, RoutedEventArgs e) =>
        AddMessage(MessageRole.System, "Ovozli input tez kunda...");

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _extApiCts?.Cancel();
        if (_hub?.State == HubConnectionState.Connected)
            await _hub.InvokeAsync("CancelRequest", _sessionId);
        SetBusy(false);
        SetStatus("Bekor qilindi");
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _isAutoMode = ModeAuto?.IsChecked == true;
        SetStatus(_isAutoMode ? "Auto rejim" : "Ask rejim");
    }

    private void ModelCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ModelCombo.SelectedItem is not string model) return;
        _currentModel = model;

        if (ExternalApiClient.IsExternalModel(model))
        {
            var provider = ExternalApiClient.ProviderLabel(model);
            ProviderText.Text = $"{provider} / {model}";
            ApiKeyProviderLabel.Text = $"🔑 {provider} API Kaliti";

            // Saqlangan kalit varsa ko'rsatmasak ham, kalit yo'q bo'lsa popupni och
            var key = _apiKeyStore.Get(provider.ToLower());
            if (string.IsNullOrEmpty(key))
                ShowApiKeyPopup();
            else
                ApiKeyInput.Password = key; // to'ldirib qo'y (edit uchun)
        }
        else
        {
            ProviderText.Text = $"ollama / {model}";
            ApiKeyPopup.IsOpen = false;
        }
    }

    private void ShowApiKeyPopup()
    {
        ApiKeyPopup.IsOpen = true;
        ApiKeyInput.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
            new Action(() => ApiKeyInput.Focus()));
    }

    private void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyInput.Password.Trim();
        if (!string.IsNullOrEmpty(key) && ExternalApiClient.IsExternalModel(_currentModel))
        {
            var provider = ExternalApiClient.ProviderLabel(_currentModel).ToLower();
            _apiKeyStore.Set(provider, key);
            AddMessage(MessageRole.System,
                $"✓ {ExternalApiClient.ProviderLabel(_currentModel)} API kaliti saqlandi");
        }
        ApiKeyPopup.IsOpen = false;
    }

    private void CloseApiKey_Click(object sender, RoutedEventArgs e)
        => ApiKeyPopup.IsOpen = false;

    private void ApiKeyInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return || e.Key == Key.Enter)
        {
            var key = ApiKeyInput.Password.Trim();
            if (!string.IsNullOrEmpty(key) && ExternalApiClient.IsExternalModel(_currentModel))
            {
                var provider = ExternalApiClient.ProviderLabel(_currentModel).ToLower();
                _apiKeyStore.Set(provider, key);
                AddMessage(MessageRole.System,
                    $"✓ {ExternalApiClient.ProviderLabel(_currentModel)} API kaliti saqlandi");
            }
            ApiKeyPopup.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ApiKeyPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    // Activity bar buttons
    private void ActFiles_Click(object sender, RoutedEventArgs e)
        => SidebarCol.Width = SidebarCol.Width.Value > 10
            ? new GridLength(0)
            : new GridLength(240);

    private void ActHistory_Click(object sender, RoutedEventArgs e) { }
    private void Settings_Click(object sender, RoutedEventArgs e) { }

    // ═══════════════════════════════════════════
    // SESSION TABS (VS Code uslubi)
    // ═══════════════════════════════════════════

    private void AddSessionTab(string sessionId, string title, bool activate)
    {
        var tab = new Border
        {
            Tag = sessionId,
            Height = 36,
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var label = new TextBlock
        {
            Text = $"  {title}  ",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = FindResource("Brush.Text.Muted") as Brush
        };

        var closeBtn = new Button
        {
            Content = "✕",
            Style   = FindResource("Btn.Ghost") as Style,
            Width = 18, Height = 18, FontSize = 9,
            Padding = new Thickness(0),
            Margin  = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = FindResource("Brush.Text.Muted") as Brush
        };
        closeBtn.Click += (_, e) => { e.Handled = true; RemoveSessionTab(tab); };

        row.Children.Add(label);
        row.Children.Add(closeBtn);
        tab.Child = row;

        tab.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Source is Button) return;
            SwitchToSession(sessionId);
        };

        SessionTabs.Children.Add(tab);

        if (activate)
            SwitchToSession(sessionId);
    }

    private void SwitchToSession(string sessionId)
    {
        // Joriy sessiyani cache ga saqlash
        SaveCurrentToCache();

        _sessionId = sessionId;
        MessagesPanel.Children.Clear();
        _streamBubble = null;
        _streamBuffer.Clear();

        // Cache dan yoki DB dan yuklash
        if (_sessionCache.TryGetValue(sessionId, out var cached))
        {
            foreach (var (role, text, mdl) in cached)
                AddMessage(role, text, mdl, persist: false);
        }
        else
        {
            var msgs = _chatStore.LoadSession(sessionId);
            foreach (var (roleStr, text, mdl) in msgs)
            {
                var role = roleStr == "user" ? MessageRole.User
                         : roleStr == "assistant" ? MessageRole.Assistant
                         : MessageRole.System;
                AddMessage(role, text, mdl, persist: false);
            }
        }

        UpdateSessionTabsUI();
    }

    private void SaveCurrentToCache()
    {
        var list = new List<(MessageRole, string, string?)>();
        foreach (var child in MessagesPanel.Children.OfType<ChatBubble>())
            if (child.CachedRole.HasValue)
                list.Add((child.CachedRole.Value, child.CachedText ?? "", child.CachedModel));
        _sessionCache[_sessionId] = list;
    }

    private void RemoveSessionTab(Border tab)
    {
        var removingId = tab.Tag?.ToString() ?? "";
        SessionTabs.Children.Remove(tab);
        _sessionCache.Remove(removingId);

        if (SessionTabs.Children.Count == 0)
        {
            // Hech qanday tab qolmadi — yangi bo'sh sessiya
            var newId = GenerateSessionId();
            MessagesPanel.Children.Clear();
            _sessionId = newId;
            AddSessionTab(newId, "Sessiya 1", activate: false);
            UpdateSessionTabsUI();
        }
        else if (removingId == _sessionId)
        {
            // Aktiv tab yopildi — oxirgi tabga o'tish
            var last = SessionTabs.Children.OfType<Border>().LastOrDefault();
            if (last?.Tag is string id) SwitchToSession(id);
        }
    }

    private void UpdateSessionTabsUI()
    {
        var activeBlue = FindResource("Brush.Accent.Blue") as Brush;
        var mutedBrush = FindResource("Brush.Text.Muted") as Brush;
        var primaryBrush = FindResource("Brush.Text.Primary") as Brush;
        var activeBg = FindResource("Brush.BG.Active") as Brush;

        foreach (var tab in SessionTabs.Children.OfType<Border>())
        {
            var isActive = tab.Tag?.ToString() == _sessionId;
            tab.Background = isActive ? activeBg : Brushes.Transparent;
            tab.BorderBrush = isActive ? activeBlue : Brushes.Transparent;
            tab.BorderThickness = new Thickness(0, 0, 0, isActive ? 2 : 0);

            if (tab.Child is StackPanel sp && sp.Children[0] is TextBlock lbl)
                lbl.Foreground = isActive ? primaryBrush : mutedBrush;
        }
    }

    // ═══════════════════════════════════════════
    // MESSAGES
    // ═══════════════════════════════════════════

    private void AddMessage(MessageRole role, string text, string? model = null, bool persist = true)
    {
        var bubble = new ChatBubble(role, text, model);
        MessagesPanel.Children.Add(bubble);
        ChatScroll.ScrollToEnd();

        if (persist && role is MessageRole.User or MessageRole.Assistant)
            _chatStore.SaveMessage(_sessionId, role.ToString().ToLower(), text, model);
    }

    private void RestoreLastSession()
    {
        var lastId = _chatStore.GetLastSessionId();
        if (lastId == null) return;

        _sessionId = lastId;
        var messages = _chatStore.LoadSession(lastId);
        if (messages.Count == 0) return;

        foreach (var (roleStr, text, mdl) in messages)
        {
            var role = roleStr switch
            {
                "user"      => MessageRole.User,
                "assistant" => MessageRole.Assistant,
                _           => MessageRole.System
            };
            AddMessage(role, text, mdl, persist: false);
        }
        AddMessage(MessageRole.System, $"── Oldingi sessiya tiklandi [{lastId}] ──", persist: false);
    }

    // ═══════════════════════════════════════════
    // WINDOW CONTROLS
    // ═══════════════════════════════════════════

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // PreviewMouseLeftButtonDown — check that source is not an interactive element
        if (IsInteractiveSource(e.OriginalSource as DependencyObject)) return;
        if (e.ClickCount == 2) Maximize_Click(sender, e);
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private static bool IsInteractiveSource(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is Button or TextBox or PasswordBox or ComboBox or
                    RadioButton or CheckBox or Slider or
                    System.Windows.Controls.Primitives.ScrollBar) return true;
            // Session tabs have Cursor=Hand
            if (d is FrameworkElement { Cursor: { } c } && c == Cursors.Hand) return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            if (d is Window) break;
        }
        return false;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ═══════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        var greenBrush  = FindResource("Brush.Accent.Green")  as Brush;
        var orangeBrush = FindResource("Brush.Accent.Orange") as Brush;
        StatusDot.Background = text == "Tayyor" ? greenBrush : orangeBrush;
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        SendBtn.IsEnabled  = !busy;
        StopBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AiDot.Background   = busy
            ? FindResource("Brush.Accent.Orange") as Brush
            : FindResource("Brush.Accent.Green")  as Brush;
        if (busy)
        {
            AiPanelStatus.Text     = "Ishlayapti...";
            ActivityBar.Visibility = Visibility.Visible;
            ActivityText.Text      = "Ishlayapti...";
        }
        else
        {
            ActivityBar.Visibility = Visibility.Collapsed;
            AiPanelStatus.Text     = "AI Agent";
        }
    }

    private void SetActivityText(string toolName, string preview)
    {
        Dispatcher.InvokeAsync(() =>
        {
            ActivityText.Text = $"{toolName}  {preview}";
        });
    }

    private static string GenerateSessionId() =>
        Guid.NewGuid().ToString("N")[..8];
}
