using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Markdig;
using Markdig.Wpf;

namespace WA.App.Controls;

/// <summary>
/// Claude Code / Cursor uslubidagi agent turn konteyner.
/// Bir turn = bitta bubble: fikrlash + tool steplar (yig'ilib-yoyiladi) + yakuniy markdown javob.
/// </summary>
public class AgentResponseBubble : Border
{
    // ── Public step interface ────────────────────────────────────────
    public interface IAgentStep
    {
        void Complete(string result, bool success, long elapsedMs);
    }

    // ── Private fields ───────────────────────────────────────────────
    private readonly StackPanel      _root;
    private readonly StackPanel      _stepsPanel;
    private readonly TextBlock       _spinnerLabel;
    private readonly TextBlock       _statusLabel;
    private readonly DispatcherTimer _spinnerTimer;
    private          MarkdownViewer? _mdViewer;
    private          string          _cachedText = "";
    private          int             _spinFrame;

    private static readonly string[] SpinFrames = ["⟳", "◌", "○", "◌"];

    // ── Constructor ──────────────────────────────────────────────────
    public AgentResponseBubble()
    {
        var app = Application.Current;

        HorizontalAlignment = HorizontalAlignment.Stretch;
        Background   = app?.FindResource("Brush.BG.Overlay") as Brush
                       ?? new SolidColorBrush(Color.FromRgb(30, 32, 48));
        CornerRadius    = new CornerRadius(3, 12, 12, 12);
        Padding         = new Thickness(12, 10, 12, 10);
        Margin          = new Thickness(8, 3, 8, 3);
        BorderThickness = new Thickness(1);
        BorderBrush     = new SolidColorBrush(Color.FromArgb(50, 89, 179, 250));

        _root = new StackPanel();

        // ── Header: spinner + status ─────────────────────────────────
        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _spinnerLabel = new TextBlock
        {
            Text      = "⟳",
            FontSize  = 13,
            Foreground = app?.FindResource("Brush.Accent.Blue") as Brush ?? Brushes.CornflowerBlue,
            VerticalAlignment = VerticalAlignment.Center,
            Margin    = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(_spinnerLabel, 0);
        header.Children.Add(_spinnerLabel);

        _statusLabel = new TextBlock
        {
            Text      = "O'ylayapman...",
            FontSize  = 11,
            Foreground = app?.FindResource("Brush.Text.Muted") as Brush ?? Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(_statusLabel, 1);
        header.Children.Add(_statusLabel);

        _root.Children.Add(header);

        // ── Tool steps panel ─────────────────────────────────────────
        _stepsPanel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        _root.Children.Add(_stepsPanel);

        Child = _root;

        // ── Spinner animation ────────────────────────────────────────
        _spinnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        _spinnerTimer.Tick += (_, _) =>
        {
            _spinFrame = (_spinFrame + 1) % SpinFrames.Length;
            _spinnerLabel.Text = SpinFrames[_spinFrame];
        };
        _spinnerTimer.Start();
    }

    // ── Public API ───────────────────────────────────────────────────

    public void SetThinking(string status)
    {
        _statusLabel.Text = status;
        _spinnerLabel.Foreground = Application.Current?.FindResource("Brush.Accent.Blue") as Brush
                                   ?? Brushes.CornflowerBlue;
        if (!_spinnerTimer.IsEnabled)
            _spinnerTimer.Start();
    }

    public IAgentStep BeginStep(string toolName, Dictionary<string, object?> args)
    {
        var step = new AgentStep(toolName, args);
        _stepsPanel.Children.Add(step);
        return step;
    }

    public void AppendToken(string token)
    {
        _cachedText += token;
        if (_mdViewer == null)
        {
            var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
            _mdViewer = new MarkdownViewer
            {
                Pipeline = pipeline,
                Markdown = _cachedText,
                Margin   = new Thickness(0, 8, 0, 0)
            };
            var app = Application.Current;
            TextElement.SetForeground(_mdViewer,
                app?.FindResource("Brush.Text.Primary") as Brush ?? Brushes.White);
            TextElement.SetFontSize(_mdViewer, 13);

            // Separator line before response text (only if we have steps)
            if (_stepsPanel.Children.Count > 0)
            {
                _root.Children.Add(new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(Color.FromArgb(50, 89, 179, 250)),
                    Margin = new Thickness(0, 6, 0, 0)
                });
            }
            _root.Children.Add(_mdViewer);
        }
        else
        {
            _mdViewer.Markdown = _cachedText;
        }
    }

    public void Finalize(string? model, string fullText)
    {
        _spinnerTimer.Stop();
        _spinnerLabel.Text = "✓";
        _spinnerLabel.Foreground = new SolidColorBrush(Color.FromRgb(166, 227, 161));
        _statusLabel.Text = model ?? "AI";

        // Agar streaming bo'lmagan bo'lsa (yakuniy javob to'g'ridan-to'g'ri kelgan)
        if (string.IsNullOrEmpty(_cachedText) && !string.IsNullOrEmpty(fullText))
            AppendToken(fullText);
    }

    // ── AgentStep — bitta tool call UI ──────────────────────────────
    private sealed class AgentStep : Border, IAgentStep
    {
        private readonly TextBlock _iconLabel;
        private readonly TextBlock _chevron;
        private readonly TextBlock _timeLabel;
        private readonly Border    _content;
        private readonly TextBox   _resultBox;
        private          bool      _expanded;

        private static readonly Dictionary<string, string> ToolIcons =
            new(StringComparer.OrdinalIgnoreCase)
        {
            ["run_command"]    = "❯",
            ["run_powershell"] = "PS❯",
            ["run_python"]     = "🐍",
            ["web_search"]     = "🔍",
            ["read_url"]       = "🌐",
            ["read_file"]      = "📄",
            ["write_file"]     = "✏",
            ["list_directory"] = "📁",
            ["search_files"]   = "🔎",
            ["delete_file"]    = "🗑",
            ["rename_file"]    = "↔",
            ["take_screenshot"]= "📷",
            ["get_system_info"]= "💻",
            ["open_app"]       = "↗",
            ["get_clipboard"]  = "📋",
            ["set_clipboard"]  = "📋",
            ["list_windows"]   = "🪟",
            ["focus_window"]   = "🎯",
            ["close_window"]   = "✕",
            ["set_volume"]     = "🔊",
            ["get_time"]       = "🕐",
            ["get_env"]        = "⚙",
            ["save_memory"]    = "🧠",
            ["recall_memory"]  = "🧠",
        };

        public AgentStep(string toolName, Dictionary<string, object?> args)
        {
            var app     = Application.Current;
            var preview = ToolActivityBubble.FormatArgsPreview(toolName, args);
            var icon    = ToolIcons.GetValueOrDefault(toolName, "⚙");

            CornerRadius    = new CornerRadius(4);
            Margin          = new Thickness(0, 2, 0, 2);
            BorderThickness = new Thickness(1);
            BorderBrush     = new SolidColorBrush(Color.FromArgb(35, 89, 179, 250));
            Background      = new SolidColorBrush(Color.FromArgb(25, 30, 35, 60));
            Cursor          = Cursors.Hand;

            var outer = new StackPanel();

            // ── Header row ──────────────────────────────────
            var header = new Grid { Margin = new Thickness(8, 5, 8, 5) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // chevron
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // status icon
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // tool name
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // preview
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // time

            _chevron = new TextBlock
            {
                Text = "▶", FontSize = 8,
                Foreground = app?.FindResource("Brush.Text.Muted") as Brush ?? Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            Grid.SetColumn(_chevron, 0);
            header.Children.Add(_chevron);

            _iconLabel = new TextBlock
            {
                Text = "⟳", FontSize = 10,
                Foreground = app?.FindResource("Brush.Accent.Cyan") as Brush ?? Brushes.Cyan,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            Grid.SetColumn(_iconLabel, 1);
            header.Children.Add(_iconLabel);

            var nameBlock = new TextBlock
            {
                Text = toolName,
                FontSize = 11,
                FontFamily = new FontFamily("Cascadia Code, Consolas"),
                Foreground = app?.FindResource("Brush.Text.Secondary") as Brush ?? Brushes.LightGray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(nameBlock, 2);
            header.Children.Add(nameBlock);

            var previewBlock = new TextBlock
            {
                Text = preview, FontSize = 10,
                FontFamily = new FontFamily("Cascadia Code, Consolas"),
                Foreground = app?.FindResource("Brush.Text.Muted") as Brush ?? Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(previewBlock, 3);
            header.Children.Add(previewBlock);

            _timeLabel = new TextBlock
            {
                Text = "", FontSize = 10,
                Foreground = app?.FindResource("Brush.Text.Muted") as Brush ?? Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(_timeLabel, 4);
            header.Children.Add(_timeLabel);

            outer.Children.Add(header);

            // ── Collapsible result content ───────────────────
            _resultBox = new TextBox
            {
                IsReadOnly  = true,
                TextWrapping = TextWrapping.Wrap,
                Background   = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontFamily   = new FontFamily("Cascadia Code, Consolas"),
                FontSize     = 11,
                Foreground   = app?.FindResource("Brush.Text.Secondary") as Brush ?? Brushes.LightGray,
                MaxHeight    = 200,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding  = new Thickness(8, 4, 8, 8),
                Cursor   = Cursors.IBeam
            };

            _content = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = new SolidColorBrush(Color.FromArgb(35, 89, 179, 250)),
                Visibility  = Visibility.Collapsed,
                Child       = _resultBox
            };
            outer.Children.Add(_content);
            Child = outer;

            // Click → toggle expand
            MouseLeftButtonUp += (_, _) =>
            {
                _expanded = !_expanded;
                _content.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
                _chevron.Text       = _expanded ? "▼" : "▶";
            };
        }

        public void Complete(string result, bool success, long elapsedMs)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                _iconLabel.Text = success ? "✓" : "✕";
                _iconLabel.Foreground = success
                    ? new SolidColorBrush(Color.FromRgb(166, 227, 161))   // green
                    : new SolidColorBrush(Color.FromRgb(243, 139, 168));  // red

                _timeLabel.Text = elapsedMs < 1000
                    ? $"{elapsedMs}ms"
                    : $"{elapsedMs / 1000.0:F1}s";

                _resultBox.Text = result.Length > 4000
                    ? result[..4000] + "\n...[qisqartirildi]"
                    : result;
            });
        }
    }
}
