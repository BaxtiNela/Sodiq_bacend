using System.Diagnostics;
using System.Text.Json;
using WA.Agent.Models;

namespace WA.Agent.Services;

/// <summary>Tool calling - CMD, PowerShell, fayl, xotira</summary>
public class ToolExecutor
{
    private readonly MemoryService _memory;

    public ToolExecutor(MemoryService memory)
    {
        _memory = memory;
    }

    public async Task<string> ExecuteAsync(ToolCall call)
    {
        var args = call.Arguments;
        string Get(string key) => args.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

        return call.Name switch
        {
            "run_command"    => await RunCmd(Get("command")),
            "run_powershell" => await RunPowershell(Get("script") is { Length: > 0 } s ? s : Get("command")),
            "read_file"      => ReadFile(Get("path")),
            "write_file"     => WriteFile(Get("path"), Get("content")),
            "list_directory" => ListDir(Get("path")),
            "search_files"   => SearchFiles(Get("directory"), Get("pattern")),
            "save_memory"    => _memory.Save(Get("key"), Get("value"), Get("category")),
            "recall_memory"  => _memory.Recall(Get("query")),
            "get_system_info"=> await GetSystemInfo(),
            "open_app"       => OpenApp(Get("name") is { Length: > 0 } n ? n : Get("app_name")),
            _                => $"Noma'lum tool: {call.Name}"
        };
    }

    // ==================== CMD / POWERSHELL ====================

    private static async Task<string> RunCmd(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "Xato: buyruq bo'sh";

        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c {command}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var result = stdout.Trim();
            if (!string.IsNullOrWhiteSpace(stderr)) result += $"\n[STDERR] {stderr.Trim()}";
            if (string.IsNullOrWhiteSpace(result)) result = $"(Buyruq bajarildi, exit: {proc.ExitCode})";
            return result.Length > 8000 ? result[..8000] + "\n...[qisqartirildi]" : result;
        }
        catch (Exception ex)
        {
            return $"CMD xato: {ex.Message}";
        }
    }

    private static async Task<string> RunPowershell(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "Xato: buyruq bo'sh";

        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var result = stdout.Trim();
            if (!string.IsNullOrWhiteSpace(stderr)) result += $"\n[STDERR] {stderr.Trim()}";
            if (string.IsNullOrWhiteSpace(result)) result = $"(Exit: {proc.ExitCode})";
            return result.Length > 8000 ? result[..8000] + "\n...[qisqartirildi]" : result;
        }
        catch (Exception ex)
        {
            return $"PowerShell xato: {ex.Message}";
        }
    }

    // ==================== FAYL ====================

    private static string ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return $"Xato: '{path}' topilmadi";
            var content = File.ReadAllText(path);
            return content.Length > 10000 ? content[..10000] + "\n...[qisqartirildi]" : content;
        }
        catch (Exception ex) { return $"Xato: {ex.Message}"; }
    }

    private static string WriteFile(string path, string content)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, content, System.Text.Encoding.UTF8);
            return $"Fayl yozildi: {path}";
        }
        catch (Exception ex) { return $"Xato: {ex.Message}"; }
    }

    private static string ListDir(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!Directory.Exists(path)) return $"Papka topilmadi: {path}";
            var entries = Directory.GetFileSystemEntries(path);
            var lines = entries.Select(e =>
            {
                var isDir = Directory.Exists(e);
                var info = isDir ? new DirectoryInfo(e) : (FileSystemInfo)new FileInfo(e);
                return $"{(isDir ? "📁" : "📄")} {info.Name}";
            });
            return string.Join("\n", lines.Take(100));
        }
        catch (Exception ex) { return $"Xato: {ex.Message}"; }
    }

    private static string SearchFiles(string directory, string pattern)
    {
        try
        {
            if (!Directory.Exists(directory)) return $"Papka topilmadi: {directory}";
            var files = Directory.GetFiles(directory, pattern ?? "*", SearchOption.AllDirectories);
            return files.Length == 0
                ? $"'{pattern}' pattern bo'yicha hech narsa topilmadi"
                : string.Join("\n", files.Take(50));
        }
        catch (Exception ex) { return $"Xato: {ex.Message}"; }
    }

    // ==================== TIZIM ====================

    private static async Task<string> GetSystemInfo()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"OS: {Environment.OSVersion}");
        sb.AppendLine($"CPU: {Environment.ProcessorCount} yadrolar");
        sb.AppendLine($"RAM: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024} MB bo'sh");
        sb.AppendLine($"Foydalanuvchi: {Environment.UserName}");
        sb.AppendLine($"Kompyuter: {Environment.MachineName}");

        var diskResult = await RunCmd("wmic logicaldisk get size,freespace,caption");
        sb.AppendLine($"Disklar:\n{diskResult}");
        return sb.ToString();
    }

    private static string OpenApp(string appName)
    {
        try
        {
            Process.Start(new ProcessStartInfo(appName) { UseShellExecute = true });
            return $"'{appName}' ochildi";
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c start {appName}")
                    { UseShellExecute = false, CreateNoWindow = true });
                return $"'{appName}' ishga tushirildi";
            }
            catch (Exception ex) { return $"Xato: {ex.Message}"; }
        }
    }

    // ==================== TOOL DEFINITIONS ====================

    public static List<ToolDefinition> GetAllTools() =>
    [
        new("run_command", "Windows CMD buyrug'ini bajarish va natijani olish",
            Params("command", "string", "CMD buyrug'i (masalan: dir, ipconfig, python --version)")),

        new("run_powershell", "PowerShell buyrug'ini bajarish",
            Params("command", "string", "PowerShell buyrug'i")),

        new("read_file", "Faylni o'qish",
            Params("path", "string", "To'liq fayl yo'li")),

        new("write_file", "Faylga yozish yoki yangi fayl yaratish",
            new() {
                ["type"] = "object",
                ["properties"] = new Dictionary<string,object> {
                    ["path"] = new Dictionary<string,object>{["type"]="string",["description"]="Fayl yo'li"},
                    ["content"] = new Dictionary<string,object>{["type"]="string",["description"]="Yoziladigan mazmun"}
                },
                ["required"] = new[]{"path","content"}
            }),

        new("list_directory", "Papka ichidagi fayllar ro'yxati",
            Params("path", "string", "Papka yo'li (bo'sh bo'lsa home papka)")),

        new("search_files", "Papkadan fayl qidirish (*.py, *.txt kabi)",
            new() {
                ["type"] = "object",
                ["properties"] = new Dictionary<string,object> {
                    ["directory"] = new Dictionary<string,object>{["type"]="string",["description"]="Qidiruv papkasi"},
                    ["pattern"] = new Dictionary<string,object>{["type"]="string",["description"]="Pattern (*.py, *.txt)"}
                },
                ["required"] = new[]{"directory","pattern"}
            }),

        new("get_system_info", "Tizim ma'lumotlari: CPU, RAM, disk, foydalanuvchi",
            new() { ["type"] = "object", ["properties"] = new Dictionary<string,object>() }),

        new("open_app", "Ilovani ochish",
            Params("app_name", "string", "Ilova nomi (notepad, chrome, code, ...)")),

        new("save_memory", "Muhim ma'lumotni xotirada saqlash (keyingi sessiyalarda ham eslab qoladi)",
            new() {
                ["type"] = "object",
                ["properties"] = new Dictionary<string,object> {
                    ["key"] = new Dictionary<string,object>{["type"]="string",["description"]="Kalit"},
                    ["value"] = new Dictionary<string,object>{["type"]="string",["description"]="Qiymat"},
                    ["category"] = new Dictionary<string,object>{["type"]="string",["description"]="Kategoriya: fact, skill, preference"}
                },
                ["required"] = new[]{"key","value"}
            }),

        new("recall_memory", "Xotiradan ma'lumot qidirish",
            Params("query", "string", "Qidiruv so'zi")),
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
}
