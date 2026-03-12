using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WA.App.Controls;

/// <summary>AI tool callini jonli ko'rsatadi — ishlayotgan → ✓/✕. Natija nusxalanadi.</summary>
public class ToolActivityBubble : Border
{
    private readonly TextBlock _icon;
    private readonly TextBlock _nameBlock;
    private readonly TextBox   _detail;
    private readonly Button    _copyBtn;

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

        // Copy button
        _copyBtn = new Button
        {
            Content           = "⎘",
            FontSize          = 11,
            Width = 22, Height = 22,
            Padding           = new Thickness(0),
            Background        = Brushes.Transparent,
            BorderThickness   = new Thickness(0),
            Foreground        = muted,
            Cursor            = Cursors.Hand,
            ToolTip           = "Nusxa olish",
            IsTabStop         = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        _copyBtn.Click += CopyBtn_Click;

        // Header: icon + name + spacer + copy
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_icon,      0);
        Grid.SetColumn(_nameBlock, 1);
        Grid.SetColumn(_copyBtn,   3);
        header.Children.Add(_icon);
        header.Children.Add(_nameBlock);
        header.Children.Add(_copyBtn);

        // Detail — selectable TextBox
        _detail = new TextBox
        {
            Text            = argsPreview,
            FontSize        = 11,
            FontFamily      = mono,
            Foreground      = muted,
            TextWrapping    = TextWrapping.Wrap,
            MaxWidth        = 560,
            Margin          = new Thickness(20, 3, 0, 0),
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            IsReadOnly      = true,
            IsTabStop       = false,
            Cursor          = Cursors.IBeam,
            Padding         = new Thickness(0),
            VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

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
            // Show full result (up to 500 chars) in selectable box
            _detail.Text = result.Length > 500 ? result[..500] + "…" : result;
        });
    }

    private void CopyBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_detail.Text);
            _copyBtn.Content    = "✓";
            _copyBtn.Foreground = new SolidColorBrush(Color.FromRgb(166, 227, 161));
            var timer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                _copyBtn.Content    = "⎘";
                _copyBtn.Foreground = new SolidColorBrush(Color.FromRgb(147, 153, 178));
                timer.Stop();
            };
            timer.Start();
        }
        catch { }
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

        return toolName switch
        {
            "run_command"    => Short(Get("command")),
            "run_powershell" => Short(Get("script").Length > 0 ? Get("script") : Get("command")),
            "read_file"      => Get("path"),
            "write_file"     => $"{Get("path")}  ({Get("content").Split('\n').Length} qator)",
            "list_directory" => Get("path"),
            "search_files"   => $"{Get("directory")} / {Get("pattern")}",
            "open_app"       => Get("name").Length > 0 ? Get("name") : Get("app_name"),
            "save_memory"    => $"{Get("key")} = {Short(Get("value"), 60)}",
            "recall_memory"  => Get("query"),
            _ => Short(string.Join("  |  ", args.Take(3).Select(p => $"{p.Key}: {p.Value?.ToString()}")))
        };
    }
}
