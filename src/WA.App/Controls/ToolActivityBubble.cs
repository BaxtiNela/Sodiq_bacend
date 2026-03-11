using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WA.App.Controls;

/// <summary>AI tool callini jonli ko'rsatadi — ishlayotgan → ✓/✕ holati</summary>
public class ToolActivityBubble : Border
{
    private readonly TextBlock _icon;
    private readonly TextBlock _nameBlock;
    private readonly TextBlock _detail;

    public ToolActivityBubble(string toolName, string argsPreview)
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        CornerRadius        = new CornerRadius(6);
        BorderThickness     = new Thickness(1);
        Padding             = new Thickness(12, 7, 12, 7);
        Margin              = new Thickness(8, 2, 8, 2);
        Background          = new SolidColorBrush(Color.FromArgb(30,  137, 220, 235));
        BorderBrush         = new SolidColorBrush(Color.FromArgb(80,  137, 220, 235));

        var cyan  = new SolidColorBrush(Color.FromRgb(137, 220, 235));
        var muted = new SolidColorBrush(Color.FromRgb(147, 153, 178));
        var mono  = new FontFamily("Cascadia Code, Consolas");

        _icon = new TextBlock
        {
            Text = "⟳", FontSize = 13,
            Foreground = cyan,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        };
        _nameBlock = new TextBlock
        {
            Text = ToolLabel(toolName), FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = cyan,
            VerticalAlignment = VerticalAlignment.Center
        };
        _detail = new TextBlock
        {
            Text = argsPreview, FontSize = 11,
            FontFamily = mono,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
            Margin = new Thickness(20, 3, 0, 0)
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(_icon);
        header.Children.Add(_nameBlock);

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(_detail);
        Child = panel;
    }

    public void SetDone(string result, bool success)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (success)
            {
                _icon.Text            = "✓";
                _icon.Foreground      = new SolidColorBrush(Color.FromRgb(166, 227, 161));
                _nameBlock.Foreground = new SolidColorBrush(Color.FromRgb(166, 227, 161));
                Background            = new SolidColorBrush(Color.FromArgb(25, 100, 210, 100));
                BorderBrush           = new SolidColorBrush(Color.FromArgb(80, 100, 210, 100));
            }
            else
            {
                _icon.Text            = "✕";
                _icon.Foreground      = new SolidColorBrush(Color.FromRgb(243, 139, 168));
                _nameBlock.Foreground = new SolidColorBrush(Color.FromRgb(243, 139, 168));
                Background            = new SolidColorBrush(Color.FromArgb(25, 210, 100, 100));
                BorderBrush           = new SolidColorBrush(Color.FromArgb(80, 210, 100, 100));
            }
            var preview = result.Length > 220 ? result[..220] + "…" : result;
            _detail.Text = preview;
        });
    }

    public static string ToolLabel(string name) => name switch
    {
        "run_command"     => "⚡ CMD",
        "run_powershell"  => "⚡ PowerShell",
        "read_file"       => "📄 Fayl o'qish",
        "write_file"      => "✏  Fayl yozish",
        "list_directory"  => "📁 Papka ko'rish",
        "search_files"    => "🔍 Fayl qidirish",
        "get_system_info" => "💻 Tizim ma'lumoti",
        "open_app"        => "🚀 Ilova ochish",
        "save_memory"     => "🧠 Xotiraga saqlash",
        "recall_memory"   => "🧠 Xotira qidirish",
        _                 => $"⚙  {name}"
    };

    public static string FormatArgsPreview(string toolName, Dictionary<string, object?> args)
    {
        string Get(string k) => args.TryGetValue(k, out var v) ? v?.ToString() ?? "" : "";
        string Short(string s, int max = 100) => s.Length > max ? s[..max] + "…" : s;

        switch (toolName)
        {
            case "run_command":    return Short(Get("command"));
            case "run_powershell": var ps = Get("script"); return Short(ps.Length > 0 ? ps : Get("command"));
            case "read_file":      return Get("path");
            case "write_file":     var cnt = Get("content"); return $"{Get("path")}  ({cnt.Split('\n').Length} qator)";
            case "list_directory": return Get("path");
            case "search_files":   return $"{Get("directory")} / {Get("pattern")}";
            case "open_app":       var n = Get("name"); return n.Length > 0 ? n : Get("app_name");
            case "save_memory":    return $"{Get("key")} = {Short(Get("value"), 60)}";
            case "recall_memory":  return Get("query");
            default:
                return Short(string.Join("  |  ",
                    args.Take(3).Select(p => $"{p.Key}: {p.Value?.ToString()}")));
        }
    }
}
