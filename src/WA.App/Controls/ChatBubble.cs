using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace WA.App.Controls;

public enum MessageRole { User, Assistant, Tool, System }

/// <summary>Chat xabari ko'rinishi - Warp/Claude uslubida</summary>
public class ChatBubble : Border
{
    public MessageRole? CachedRole  { get; private set; }
    public string?      CachedText  { get; private set; }
    public string?      CachedModel { get; private set; }

    public ChatBubble(MessageRole role, string text, string? model = null)
    {
        CachedRole  = role;
        CachedText  = text;
        CachedModel = model;
        Margin = new Thickness(8, 3, 8, 3);

        var app = Application.Current;

        switch (role)
        {
            case MessageRole.User:
                BuildUserBubble(text, app);
                break;
            case MessageRole.Assistant:
                BuildAssistantBubble(text, model, app);
                break;
            case MessageRole.Tool:
                BuildToolBubble(text, app);
                break;
            case MessageRole.System:
                BuildSystemBubble(text, app);
                break;
        }
    }

    private void BuildUserBubble(string text, Application? app)
    {
        HorizontalAlignment = HorizontalAlignment.Right;
        Background = new SolidColorBrush(Color.FromRgb(43, 58, 91));
        CornerRadius = new CornerRadius(12, 12, 3, 12);
        Padding = new Thickness(12, 8, 12, 8);
        MaxWidth = 440;

        Child = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = app?.FindResource("Brush.Text.Primary") as Brush ?? Brushes.White,
            FontSize = 13,
            LineHeight = 20
        };
    }

    private void BuildAssistantBubble(string text, string? model, Application? app)
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Background = app?.FindResource("Brush.BG.Overlay") as Brush
                     ?? new SolidColorBrush(Color.FromRgb(36, 39, 58));
        CornerRadius = new CornerRadius(3, 12, 12, 12);
        Padding = new Thickness(12, 10, 12, 10);

        var panel = new StackPanel();

        // Model tag
        if (!string.IsNullOrEmpty(model))
        {
            panel.Children.Add(new TextBlock
            {
                Text = model,
                FontSize = 10,
                Foreground = app?.FindResource("Brush.Accent.Blue") as Brush ?? Brushes.CornflowerBlue,
                Margin = new Thickness(0, 0, 0, 4)
            });
        }

        // Message text with markdown-like formatting
        panel.Children.Add(BuildAssistantTextBlock(text, app));

        Child = panel;
    }

    private void BuildToolBubble(string text, Application? app)
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Background = new SolidColorBrush(Color.FromArgb(60, 137, 220, 235));
        CornerRadius = new CornerRadius(6);
        BorderBrush = app?.FindResource("Brush.Accent.Cyan") as Brush ?? Brushes.Cyan;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(10, 6, 10, 6);
        Margin = new Thickness(8, 2, 8, 2);

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = "⚙ ",
            Foreground = app?.FindResource("Brush.Accent.Cyan") as Brush ?? Brushes.Cyan,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 4, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = app?.FindResource("Brush.Text.Secondary") as Brush ?? Brushes.LightGray,
            FontFamily = new FontFamily("Cascadia Code, Consolas"),
            FontSize = 11,
            MaxWidth = 280
        });

        Child = panel;
    }

    private void BuildSystemBubble(string text, Application? app)
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        Background = Brushes.Transparent;
        Padding = new Thickness(0, 4, 0, 4);

        Child = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = app?.FindResource("Brush.Text.Muted") as Brush ?? Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center
        };
    }

    // Streaming uchun — mavjud assistant bubble'ga token qo'shish
    private TextBlock? _mainTextBlock;

    public void AppendText(string token)
    {
        if (_mainTextBlock != null)
        {
            _mainTextBlock.Inlines.Add(new Run(token));
        }
    }

    private TextBlock BuildAssistantTextBlock(string text, Application? app)
    {
        var tb = BuildMarkdownText(text, app);
        _mainTextBlock = tb;
        return tb;
    }

    private static TextBlock BuildMarkdownText(string text, Application? app)
    {
        // Oddiy markdown: **bold**, `code`, newlines
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = app?.FindResource("Brush.Text.Primary") as Brush ?? Brushes.White,
            FontSize = 13,
            LineHeight = 21
        };

        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            if (tb.Inlines.Count > 0)
                tb.Inlines.Add(new LineBreak());

            // Code block detection
            if (line.StartsWith("```") || line.StartsWith("    "))
            {
                var codeRun = new Run(line.TrimStart('`', ' '))
                {
                    FontFamily = new FontFamily("Cascadia Code, Consolas"),
                    FontSize = 12,
                    Foreground = app?.FindResource("Brush.Accent.Orange") as Brush ?? Brushes.Orange
                };
                tb.Inlines.Add(codeRun);
            }
            else
            {
                tb.Inlines.Add(new Run(line));
            }
        }

        return tb;
    }
}
