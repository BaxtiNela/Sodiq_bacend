using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Markdig;
using Markdig.Wpf;

namespace WA.App.Controls;

public enum MessageRole { User, Assistant, Tool, System }

/// <summary>Chat xabari — matn tanlanishi va nusxalash qo'llab-quvvatlanadi</summary>
public class ChatBubble : Border
{
    public MessageRole? CachedRole  { get; private set; }
    public string?      CachedText  { get; private set; }
    public string?      CachedModel { get; private set; }

    // Streaming uchun — mavjud assistant TextBox ga token qo'shish
    private TextBox? _mainBox;
    private MarkdownViewer? _mdViewer;

    public ChatBubble(MessageRole role, string text, string? model = null)
    {
        CachedRole  = role;
        CachedText  = text;
        CachedModel = model;
        Margin = new Thickness(8, 3, 8, 3);

        var app = Application.Current;

        switch (role)
        {
            case MessageRole.User:      BuildUserBubble(text, app);             break;
            case MessageRole.Assistant: BuildAssistantBubble(text, model, app); break;
            case MessageRole.Tool:      BuildToolBubble(text, app);             break;
            case MessageRole.System:    BuildSystemBubble(text, app);           break;
        }
    }

    // ── User ──────────────────────────────────────────────────────────
    private void BuildUserBubble(string text, Application? app)
    {
        HorizontalAlignment = HorizontalAlignment.Right;
        Background   = new SolidColorBrush(Color.FromRgb(43, 58, 91));
        CornerRadius = new CornerRadius(12, 12, 3, 12);
        Padding  = new Thickness(12, 8, 12, 8);
        MaxWidth = 440;

        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tb = MakeReadOnlyBox(text,
            app?.FindResource("Brush.Text.Primary") as Brush ?? Brushes.White, 13);
        Grid.SetColumn(tb, 0);
        panel.Children.Add(tb);

        var copyBtn = MakeCopyButton(() => CachedText ?? "", app);
        Grid.SetColumn(copyBtn, 1);
        panel.Children.Add(copyBtn);

        Child = panel;
    }

    // ── Assistant ─────────────────────────────────────────────────────
    private void BuildAssistantBubble(string text, string? model, Application? app)
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Background   = app?.FindResource("Brush.BG.Overlay") as Brush
                       ?? new SolidColorBrush(Color.FromRgb(36, 39, 58));
        CornerRadius = new CornerRadius(3, 12, 12, 12);
        Padding  = new Thickness(12, 10, 12, 10);

        var outer = new StackPanel();

        // Header: model tag + copy button
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (!string.IsNullOrEmpty(model))
        {
            var modelTag = new TextBlock
            {
                Text      = model,
                FontSize   = 10,
                Foreground = app?.FindResource("Brush.Accent.Blue") as Brush ?? Brushes.CornflowerBlue,
                Margin     = new Thickness(0, 0, 0, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(modelTag, 0);
            header.Children.Add(modelTag);
        }

        var cpyBtn = MakeCopyButton(() => CachedText ?? "", app);
        Grid.SetColumn(cpyBtn, 1);
        header.Children.Add(cpyBtn);
        outer.Children.Add(header);

        // Message text (selectable, using Markdig.Wpf)
        var pipeline = new Markdig.MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();

        _mdViewer = new MarkdownViewer
        {
            Pipeline = pipeline,
            Markdown = text,
            Margin = new Thickness(0, 4, 0, 0)
        };
        // Apply text styling rules for FlowDocument
        TextElement.SetForeground(_mdViewer, app?.FindResource("Brush.Text.Primary") as Brush ?? Brushes.White);
        TextElement.SetFontSize(_mdViewer, 13);
        
        outer.Children.Add(_mdViewer);

        Child = outer;
    }

    // ── Tool ──────────────────────────────────────────────────────────
    private void BuildToolBubble(string text, Application? app)
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Background      = new SolidColorBrush(Color.FromArgb(60, 137, 220, 235));
        CornerRadius    = new CornerRadius(6);
        BorderBrush     = app?.FindResource("Brush.Accent.Cyan") as Brush ?? Brushes.Cyan;
        BorderThickness = new Thickness(1);
        Padding  = new Thickness(10, 6, 10, 6);
        Margin   = new Thickness(8, 2, 8, 2);

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = "⚙ ",
            Foreground = app?.FindResource("Brush.Accent.Cyan") as Brush ?? Brushes.Cyan,
            FontSize   = 11,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 4, 0)
        });
        panel.Children.Add(MakeReadOnlyBox(text,
            app?.FindResource("Brush.Text.Secondary") as Brush ?? Brushes.LightGray,
            11, mono: true, maxWidth: 280));

        Child = panel;
    }

    // ── System ────────────────────────────────────────────────────────
    private void BuildSystemBubble(string text, Application? app)
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        Background = Brushes.Transparent;
        Padding    = new Thickness(0, 4, 0, 4);

        Child = MakeReadOnlyBox(text,
            app?.FindResource("Brush.Text.Muted") as Brush ?? Brushes.Gray,
            11, centered: true);
    }

    // ── Streaming ─────────────────────────────────────────────────────
    public void AppendText(string token)
    {
        CachedText += token;
        if (_mainBox != null)
            _mainBox.AppendText(token);
        if (_mdViewer != null)
            _mdViewer.Markdown = CachedText ?? string.Empty;
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static TextBox MakeReadOnlyBox(
        string text, Brush fg, double fontSize,
        double lineHeight = 0, bool mono = false,
        double maxWidth = double.NaN, bool centered = false)
    {
        var tb = new TextBox
        {
            Text            = text,
            TextWrapping    = TextWrapping.Wrap,
            Foreground      = fg,
            FontSize        = fontSize,
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            IsReadOnly      = true,
            IsTabStop       = false,
            Cursor          = Cursors.IBeam,
            Padding         = new Thickness(0),
            VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        if (lineHeight > 0)
            tb.SetValue(TextBlock.LineHeightProperty, lineHeight);
        if (mono)
            tb.FontFamily = new FontFamily("Cascadia Code, Consolas");
        if (!double.IsNaN(maxWidth))
            tb.MaxWidth = maxWidth;
        if (centered)
            tb.TextAlignment = TextAlignment.Center;
        return tb;
    }

    private static Button MakeCopyButton(Func<string> getText, Application? app)
    {
        var btn = new Button
        {
            Content           = "⎘",
            FontSize          = 12,
            Width             = 24,
            Height            = 24,
            Padding           = new Thickness(0),
            Margin            = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Background        = Brushes.Transparent,
            BorderThickness   = new Thickness(0),
            Foreground        = app?.FindResource("Brush.Text.Muted") as Brush ?? Brushes.Gray,
            Cursor            = Cursors.Hand,
            ToolTip           = "Nusxa olish",
            IsTabStop         = false
        };
        btn.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(getText());
                btn.Content    = "✓";
                btn.Foreground = new SolidColorBrush(Color.FromRgb(166, 227, 161));
                var timer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromSeconds(1.5) };
                timer.Tick += (_, _) =>
                {
                    btn.Content    = "⎘";
                    btn.Foreground = app?.FindResource("Brush.Text.Muted") as Brush ?? Brushes.Gray;
                    timer.Stop();
                };
                timer.Start();
            }
            catch { }
        };
        return btn;
    }
}
