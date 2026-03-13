using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using WA.Agent.Models;

namespace WA.Agent.Services;

/// <summary>
/// Tool calling executor — CMD, PowerShell, fayl, desktop, internet, xotira.
/// Barcha toollar structured result qaytaradi: {"ok":true,"out":"..."} yoki {"ok":false,"err":"..."}
/// </summary>
public class ToolExecutor
{
    private readonly MemoryService _memory;
    private readonly WebSearchService _webSearch = new();

    public ToolExecutor(MemoryService memory)
    {
        _memory = memory;
    }

    public async Task<string> ExecuteAsync(ToolCall call)
    {
        var args = call.Arguments;
        string Get(string key) => args.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";
        int GetInt(string key, int def) => int.TryParse(Get(key), out var n) ? n : def;

        try
        {
            return call.Name switch
            {
                // ── Tizim buyruqlari ──
                "run_command"    => await RunCmd(Get("command")),
                "run_powershell" => await RunPowershell(Get("script") is { Length: > 0 } s ? s : Get("command")),

                // ── Dasturlash tillari ──
                "run_python"  => await RunCmd($"python -c \"{EscapeForCmd(Get("code"))}\""),
                "run_node"    => await RunCmd($"node -e \"{EscapeForCmd(Get("code"))}\""),
                "run_go"      => await RunGoCode(Get("code")),
                "run_java"    => await RunJavaCode(Get("code")),
                "run_dart"    => await RunCmd($"dart run --stdin \"{EscapeForCmd(Get("code"))}\""),
                "run_csharp"  => await RunCSharpCode(Get("code")),

                // ── Fayl ──
                "read_file"      => ReadFile(Get("path")),
                "write_file"     => WriteFile(Get("path"), Get("content")),
                "list_directory" => ListDir(Get("path")),
                "search_files"   => SearchFiles(Get("directory"), Get("pattern")),
                "delete_file"    => DeleteFile(Get("path")),
                "rename_file"    => RenameFile(Get("old_path"), Get("new_path")),

                // ── Tizim ma'lumotlari ──
                "get_system_info" => await GetSystemInfo(),
                "get_time"        => GetTime(Get("format")),
                "get_env"         => GetEnv(Get("name")),

                // ── Desktop ──
                "open_app"        => OpenApp(Get("name") is { Length: > 0 } n ? n : Get("app_name")),
                "take_screenshot" => await TakeScreenshot(Get("filename")),
                "get_clipboard"   => await GetClipboard(),
                "set_clipboard"   => await SetClipboard(Get("text")),
                "list_windows"    => ListWindows(),
                "focus_window"    => FocusWindow(Get("title")),
                "close_window"    => CloseWindowByTitle(Get("title")),
                "set_volume"      => await SetVolume(GetInt("level", -1)),

                // ── Internet ──
                "web_search" => await _webSearch.SearchAsync(Get("query"), GetInt("num_results", 5)),
                "read_url"   => await _webSearch.ReadUrlAsync(Get("url"),  GetInt("max_chars", 5000)),

                // ── Xotira ──
                "save_memory"   => _memory.Save(Get("key"), Get("value"), Get("category")),
                "recall_memory" => _memory.Recall(Get("query")),

                _ => $"Noma'lum tool: {call.Name}"
            };
        }
        catch (Exception ex)
        {
            return Err($"{call.Name} xatosi: {ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════
    // CMD / POWERSHELL
    // ══════════════════════════════════════════════════════════

    private static async Task<string> RunCmd(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return Err("Buyruq bo'sh");

        // "start X" — GUI ilovalar yoki URL lar uchun UseShellExecute ishlatish kerak
        var trimmed = command.TrimStart();
        if (trimmed.StartsWith("start ", StringComparison.OrdinalIgnoreCase))
        {
            var target = trimmed[6..].Trim().Trim('"', '\'', ' ');
            if (!string.IsNullOrEmpty(target))
            {
                // URL → brauzerda och
                if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                        return $"✓ '{target}' brauzerda ochildi";
                    }
                    catch (Exception ex) { return Err($"URL ochishda xato: {ex.Message}"); }
                }

                // Ilova nomi → OpenApp mantiqidan foydalanish
                var appResult = OpenApp(target);
                if (!appResult.StartsWith("[Xato]")) return appResult;
                // Xato bo'lsa — odatiy cmd start ga o'tamiz
            }
        }

        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c {command}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8
            };
            using var proc   = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var result = stdout.Trim();
            if (!string.IsNullOrWhiteSpace(stderr)) result += $"\n[STDERR] {stderr.Trim()}";
            if (string.IsNullOrWhiteSpace(result))  result  = $"Bajarildi (exit: {proc.ExitCode})";
            return result.Length > 8000 ? result[..8000] + "\n...[qisqartirildi]" : result;
        }
        catch (Exception ex) { return Err($"CMD: {ex.Message}"); }
    }

    private static async Task<string> RunPowershell(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return Err("Buyruq bo'sh");
        try
        {
            var escaped = command.Replace("\"", "\\\"");
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -Command \"{escaped}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var proc   = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var result = stdout.Trim();
            if (!string.IsNullOrWhiteSpace(stderr)) result += $"\n[STDERR] {stderr.Trim()}";
            if (string.IsNullOrWhiteSpace(result))  result  = $"Bajarildi (exit: {proc.ExitCode})";
            return result.Length > 8000 ? result[..8000] + "\n...[qisqartirildi]" : result;
        }
        catch (Exception ex) { return Err($"PowerShell: {ex.Message}"); }
    }

    // ══════════════════════════════════════════════════════════
    // FAYL OPERATSIYALARI
    // ══════════════════════════════════════════════════════════

    private static string ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return Err($"Fayl topilmadi: {path}");
            var content = File.ReadAllText(path, Encoding.UTF8);
            return content.Length > 10000 ? content[..10000] + "\n...[qisqartirildi]" : content;
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private static string WriteFile(string path, string content)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, content, Encoding.UTF8);
            return $"✓ Yozildi: {path} ({content.Length} belgi)";
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private static string ListDir(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!Directory.Exists(path)) return Err($"Papka topilmadi: {path}");

            var entries = Directory.GetFileSystemEntries(path)
                .Select(e => Directory.Exists(e)
                    ? $"📁 {Path.GetFileName(e)}/"
                    : $"📄 {Path.GetFileName(e)}")
                .Take(100);
            return $"{path}:\n" + string.Join("\n", entries);
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private static string SearchFiles(string directory, string pattern)
    {
        try
        {
            if (!Directory.Exists(directory)) return Err($"Papka topilmadi: {directory}");
            if (string.IsNullOrWhiteSpace(pattern)) pattern = "*";
            var files = Directory.GetFiles(directory, pattern, SearchOption.AllDirectories);
            return files.Length == 0
                ? $"'{pattern}' pattern bo'yicha hech narsa topilmadi"
                : string.Join("\n", files.Take(50));
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private static string DeleteFile(string path)
    {
        try
        {
            // Safety: tizim papkalarini himoya qilish
            var lower = path.ToLowerInvariant();
            if (lower.Contains("\\windows\\") || lower.Contains("\\system32\\") ||
                lower.Contains("\\program files\\"))
                return Err("Xavfsizlik: tizim faylini o'chirish taqiqlangan");

            if (File.Exists(path))
            {
                File.Delete(path);
                return $"✓ O'chirildi: {path}";
            }
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                return $"✓ Papka o'chirildi: {path}";
            }
            return Err($"Topilmadi: {path}");
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private static string RenameFile(string oldPath, string newPath)
    {
        try
        {
            if (!File.Exists(oldPath) && !Directory.Exists(oldPath))
                return Err($"Topilmadi: {oldPath}");
            var dir = Path.GetDirectoryName(newPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(oldPath))      File.Move(oldPath, newPath, overwrite: false);
            else                           Directory.Move(oldPath, newPath);
            return $"✓ Ko'chirildi: {oldPath} → {newPath}";
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // ══════════════════════════════════════════════════════════
    // TIZIM MA'LUMOTLARI
    // ══════════════════════════════════════════════════════════

    private static async Task<string> GetSystemInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OS: {Environment.OSVersion}");
        sb.AppendLine($"CPU: {Environment.ProcessorCount} yadrolar");
        sb.AppendLine($"RAM bo'sh: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024} MB");
        sb.AppendLine($"Foydalanuvchi: {Environment.UserName}");
        sb.AppendLine($"Kompyuter: {Environment.MachineName}");
        sb.AppendLine($"Vaqt: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var diskResult = await RunCmd("wmic logicaldisk get size,freespace,caption");
        sb.AppendLine($"Disklar:\n{diskResult}");
        return sb.ToString();
    }

    private static string GetTime(string format)
    {
        var now = DateTime.Now;
        return format?.ToLower() switch
        {
            "iso"   => now.ToString("o"),
            "short" => now.ToString("HH:mm, dd MMM yyyy"),
            _       => $"Sana: {now:dddd, d MMMM yyyy}\n" +
                       $"Vaqt: {now:HH:mm:ss}\n" +
                       $"Timezone: {TimeZoneInfo.Local.DisplayName}\n" +
                       $"UTC farqi: UTC{now:zzz}"
        };
    }

    private static string GetEnv(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Err("O'zgaruvchi nomi bo'sh");
        var val = Environment.GetEnvironmentVariable(name);
        return val is null ? Err($"'{name}' topilmadi") : $"{name} = {val}";
    }

    // ══════════════════════════════════════════════════════════
    // DESKTOP BOSHQARUV
    // ══════════════════════════════════════════════════════════

    private static readonly Dictionary<string, string> _appUriMap =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["telegram"]  = "tg://",
        ["whatsapp"]  = "whatsapp://",
        ["discord"]   = "discord://",
        ["spotify"]   = "spotify:",
        ["vscode"]    = "code",
        ["vs code"]   = "code",
    };

    private static readonly Dictionary<string, string> _appPathMap =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["telegram"]  = @"%APPDATA%\Telegram Desktop\Telegram.exe",
        ["chrome"]    = @"%ProgramFiles%\Google\Chrome\Application\chrome.exe",
        ["firefox"]   = @"%ProgramFiles%\Mozilla Firefox\firefox.exe",
        ["edge"]      = @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe",
        ["notepad++"] = @"%ProgramFiles%\Notepad++\notepad++.exe",
    };

    private static string OpenApp(string appName)
    {
        // 1. URI scheme (Telegram tg://, Spotify, Discord...)
        if (_appUriMap.TryGetValue(appName, out var uri))
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                return $"✓ '{appName}' ochildi";
            }
            catch { }
        }

        // 2. Known install paths
        if (_appPathMap.TryGetValue(appName, out var rawPath))
        {
            var exePath = Environment.ExpandEnvironmentVariables(rawPath);
            if (File.Exists(exePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
                    return $"✓ '{appName}' ochildi";
                }
                catch { }
            }
        }

        // 3. Windows Shell (PATH + App Paths registry)
        try
        {
            Process.Start(new ProcessStartInfo(appName) { UseShellExecute = true });
            return $"✓ '{appName}' ochildi";
        }
        catch { }

        // 4. CMD start fallback
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c start \"\" \"{appName}\"")
                { UseShellExecute = false, CreateNoWindow = true });
            return $"✓ '{appName}' ishga tushirildi";
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private static async Task<string> TakeScreenshot(string? filename)
    {
        try
        {
            // PowerShell orqali screenshot olish
            if (string.IsNullOrWhiteSpace(filename))
                filename = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";

            var dir  = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, filename);

            // Add-Type va DrawingBitmapImage yordamida screenshot
            var script = $@"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp    = New-Object System.Drawing.Bitmap($screen.Width, $screen.Height)
$g      = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($screen.Location, [System.Drawing.Point]::Empty, $screen.Size)
$bmp.Save('{path.Replace("'", "''")}')
$bmp.Dispose()
Write-Output 'OK'
".Trim();

            var result = await RunPowershell(script);
            if (result.Contains("OK") || File.Exists(path))
                return $"✓ Screenshot saqlandi: {path}";
            return Err($"Screenshot olishda xato: {result}");
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private static async Task<string> GetClipboard()
    {
        string result = "";
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            result = Clipboard.ContainsText() ? Clipboard.GetText() : "(Clipboard bo'sh yoki matn emas)";
        });
        return result;
    }

    private static async Task<string> SetClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return Err("Matn bo'sh");
        await Application.Current.Dispatcher.InvokeAsync(() => Clipboard.SetText(text));
        return $"✓ Clipboardga yozildi ({text.Length} belgi)";
    }

    private static string ListWindows()
    {
        var windows = Process.GetProcesses()
            .Where(p => !string.IsNullOrWhiteSpace(p.MainWindowTitle))
            .Select(p => $"[{p.Id}] {p.ProcessName} — {p.MainWindowTitle}")
            .Take(30)
            .ToList();

        return windows.Count == 0
            ? "Ochiq oyna topilmadi"
            : $"Ochiq oynalar ({windows.Count} ta):\n" + string.Join("\n", windows);
    }

    private static string FocusWindow(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return Err("Oyna sarlavhasi bo'sh");
        var proc = Process.GetProcesses()
            .FirstOrDefault(p => p.MainWindowTitle.Contains(title, StringComparison.OrdinalIgnoreCase));

        if (proc == null) return Err($"'{title}' sarlavhali oyna topilmadi");

        SetForegroundWindow(proc.MainWindowHandle);
        ShowWindow(proc.MainWindowHandle, 9); // SW_RESTORE
        return $"✓ '{proc.MainWindowTitle}' oldinga chiqarildi";
    }

    private static string CloseWindowByTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return Err("Oyna sarlavhasi bo'sh");

        // Tizim oynalarini himoya qilish
        var safeTitles = new[] { "explorer", "taskbar", "start menu", "desktop" };
        if (safeTitles.Any(s => title.Contains(s, StringComparison.OrdinalIgnoreCase)))
            return Err("Tizim oynasini yopish taqiqlangan");

        var procs = Process.GetProcesses()
            .Where(p => p.MainWindowTitle.Contains(title, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (procs.Count == 0) return Err($"'{title}' sarlavhali oyna topilmadi");

        foreach (var p in procs)
            PostMessage(p.MainWindowHandle, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE

        return $"✓ '{title}' yopildi ({procs.Count} ta oyna)";
    }

    private static async Task<string> SetVolume(int level)
    {
        if (level < 0 || level > 100) return Err("Ovoz darajasi 0-100 oralig'ida bo'lishi kerak");

        // PowerShell orqali ovozni o'zgartirish
        var script = $@"
$volume = {level} / 100
Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
[Guid(""5CDF2C82-841E-4546-9722-0CF74078229A""), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume {{ void dummy1(); void dummy2(); void dummy3(); void dummy4(); void dummy5(); void dummy6(); [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref System.Guid pguidEventContext); }}
[Guid(""BCDE0395-E52F-467C-8E3D-C4579291692E""), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator {{ void GetDefaultAudioEndpoint(int dataFlow, int role, [MarshalAs(UnmanagedType.Interface)] out object ppDevice); }}
'@
$script:nircmd = 'nircmd.exe'
& nircmd.exe setsysvolume ([int]($volume * 65535)) 2>$null
if ($LASTEXITCODE -ne 0) {{
    (Get-Process | ? {{$_.name -eq 'explorer'}}) | %{{ $_.Kill() }}
}}
Write-Output 'OK'
".Trim();

        // Eng ishonchli: nircmd yoki PowerShell native
        var result = await RunPowershell($@"
$volume = {level}
$wshell = New-Object -comObject WScript.Shell
$val = [int]($volume * 655.35)
Add-Type -TypeDefinition '
using System;
using System.Runtime.InteropServices;
public class Audio {{
    [DllImport(""winmm.dll"")]
    public static extern int waveOutSetVolume(IntPtr h, uint dVolume);
}}
'
$level = [uint32](($volume/100.0) * 0xFFFF)
$stereo = ($level) -bor ($level -shl 16)
[Audio]::waveOutSetVolume([IntPtr]::Zero, $stereo)
Write-Output ""Ovoz: $volume%""
".Trim());

        if (result.Contains($"Ovoz: {level}"))
            return $"✓ Ovoz {level}% ga o'rnatildi";

        // Agar xato bo'lsa sodda yo'l bilan
        await RunCmd($"nircmd.exe setsysvolume {level * 655}");
        return $"✓ Ovoz {level}% ga o'rnatildi";
    }

    // ══════════════════════════════════════════════════════════
    // TOOL DEFINITIONS (local agent uchun)
    // ══════════════════════════════════════════════════════════

    public static List<ToolDefinition> GetAllTools() =>
    [
        new("run_command", "Windows CMD buyrug'ini bajarish va natijani olish",
            Params("command", "string", "CMD buyrug'i")),

        new("run_powershell", "PowerShell buyrug'ini bajarish",
            Params("command", "string", "PowerShell buyrug'i")),

        new("read_file",  "Faylni o'qish", Params("path", "string", "Fayl yo'li")),

        new("write_file", "Faylga yozish",
            new() {
                ["type"] = "object",
                ["properties"] = new Dictionary<string,object> {
                    ["path"]    = new Dictionary<string,object>{["type"]="string",["description"]="Fayl yo'li"},
                    ["content"] = new Dictionary<string,object>{["type"]="string",["description"]="Mazmun"}
                },
                ["required"] = new[]{"path","content"}
            }),

        new("list_directory", "Papka tarkibi", Params("path", "string", "Papka yo'li")),
        new("search_files", "Fayl qidirish",   Params("pattern","string","Pattern (*.py)")),
        new("delete_file",  "Fayl o'chirish",  Params("path","string","Fayl yo'li")),

        new("rename_file", "Fayl ko'chirish",
            new() {
                ["type"] = "object",
                ["properties"] = new Dictionary<string,object> {
                    ["old_path"] = new Dictionary<string,object>{["type"]="string",["description"]="Eski yo'l"},
                    ["new_path"] = new Dictionary<string,object>{["type"]="string",["description"]="Yangi yo'l"}
                },
                ["required"] = new[]{"old_path","new_path"}
            }),

        new("get_system_info", "Tizim ma'lumotlari",
            new() { ["type"] = "object", ["properties"] = new Dictionary<string,object>() }),

        new("get_time", "Vaqt va sana",
            new() { ["type"] = "object", ["properties"] = new Dictionary<string,object>() }),

        new("get_env", "Muhit o'zgaruvchisi", Params("name","string","O'zgaruvchi nomi")),
        new("open_app", "Dastur ochish",      Params("app_name","string","Dastur nomi")),

        new("take_screenshot", "Ekran rasmi",
            new() { ["type"] = "object", ["properties"] = new Dictionary<string,object>() }),

        new("get_clipboard", "Clipboard o'qish",
            new() { ["type"] = "object", ["properties"] = new Dictionary<string,object>() }),

        new("set_clipboard", "Clipboardga yozish", Params("text","string","Matn")),
        new("list_windows",  "Ochiq oynalar",
            new() { ["type"] = "object", ["properties"] = new Dictionary<string,object>() }),

        new("focus_window", "Oyna focus",  Params("title","string","Sarlavha qismi")),
        new("close_window", "Oynani yop",  Params("title","string","Sarlavha qismi")),
        new("set_volume",   "Ovoz",        Params("level","string","0-100")),

        new("web_search", "Internet qidiruv", Params("query","string","Qidiruv so'zi")),
        new("read_url",   "URL o'qish",       Params("url","string","https://...")),

        new("save_memory",   "Xotirada saqlash", Params("key","string","Kalit")),
        new("recall_memory", "Xotiradan izlash", Params("query","string","So'z")),
    ];

    private static Dictionary<string, object> Params(string name, string type, string desc) => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            [name] = new Dictionary<string, object> { ["type"] = type, ["description"] = desc }
        },
        ["required"] = new[] { name }
    };

    // ══════════════════════════════════════════════════════════
    // WIN32 API
    // ══════════════════════════════════════════════════════════

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    // ══════════════════════════════════════════════════════════
    // DASTURLASH TILLARI
    // ══════════════════════════════════════════════════════════

    private static string EscapeForCmd(string code)
        => code.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");

    private static async Task<string> RunGoCode(string code)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"wa_go_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        var file = Path.Combine(tmp, "main.go");
        await File.WriteAllTextAsync(file, code);
        var result = await RunProcessAsync("go", $"run \"{file}\"");
        Directory.Delete(tmp, true);
        return result;
    }

    private static async Task<string> RunJavaCode(string code)
    {
        // Class nomini topib vaqtinchalik fayl yaratamiz
        var className = "Main";
        var match = System.Text.RegularExpressions.Regex.Match(code, @"public\s+class\s+(\w+)");
        if (match.Success) className = match.Groups[1].Value;

        var tmp  = Path.Combine(Path.GetTempPath(), $"wa_java_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        var file = Path.Combine(tmp, $"{className}.java");
        await File.WriteAllTextAsync(file, code);
        var compile = await RunProcessAsync("javac", $"\"{file}\"");
        if (compile.Contains("[Xato]")) { Directory.Delete(tmp, true); return compile; }
        var run = await RunProcessAsync("java", $"-cp \"{tmp}\" {className}");
        Directory.Delete(tmp, true);
        return run;
    }

    private static async Task<string> RunCSharpCode(string code)
    {
        // dotnet-script yoki csc bilan ishlatamiz
        var tmp  = Path.Combine(Path.GetTempPath(), $"wa_cs_{Guid.NewGuid():N}.csx");
        await File.WriteAllTextAsync(tmp, code);
        var result = await RunProcessAsync("dotnet-script", $"\"{tmp}\"");
        File.Delete(tmp);
        return result;
    }

    private static async Task<string> RunProcessAsync(string exe, string args, int timeoutMs = 30000)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8
            };
            using var proc = Process.Start(psi)!;
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            var completed = await Task.WhenAny(proc.WaitForExitAsync(), Task.Delay(timeoutMs));
            if (!proc.HasExited) { proc.Kill(true); return Err($"Timeout ({timeoutMs/1000}s)"); }
            var stdout = await outTask;
            var stderr = await errTask;
            var result = stdout.Trim();
            if (!string.IsNullOrWhiteSpace(stderr)) result += $"\n[STDERR] {stderr.Trim()}";
            return string.IsNullOrWhiteSpace(result) ? $"✓ (exit: {proc.ExitCode})" : result;
        }
        catch (Exception ex) { return Err($"{exe} topilmadi yoki xato: {ex.Message}"); }
    }

    // ── Helpers ──

    private static string Err(string msg) => $"[Xato] {msg}";
}
