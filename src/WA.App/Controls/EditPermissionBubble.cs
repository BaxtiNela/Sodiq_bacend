using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WA.App.Controls;

/// <summary>write_file bajarilishidan oldin foydalanuvchidan ruxsat so'raydi</summary>
public class EditPermissionBubble : Border
{
    private readonly TaskCompletionSource<bool> _tcs = new();
    public Task<bool> Decision => _tcs.Task;

    public EditPermissionBubble(string path, string content)
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        CornerRadius        = new CornerRadius(8);
        BorderThickness     = new Thickness(1);
        Padding             = new Thickness(14, 12, 14, 12);
        Margin              = new Thickness(8, 4, 8, 4);
        Background          = new SolidColorBrush(Color.FromArgb(35, 250, 179, 135));
        BorderBrush         = new SolidColorBrush(Color.FromArgb(120, 250, 179, 135));

        var orange   = new SolidColorBrush(Color.FromRgb(250, 179, 135));
        var textMid  = new SolidColorBrush(Color.FromRgb(186, 194, 222));
        var textDim  = new SolidColorBrush(Color.FromRgb(147, 153, 178));
        var mono     = new FontFamily("Cascadia Code, Consolas");

        // Header
        var header = new TextBlock
        {
            Text = "✏  Faylni tahrirlash — ruxsat kerak",
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = orange,
            Margin = new Thickness(0, 0, 0, 6)
        };

        // Path
        var pathBlock = new TextBlock
        {
            Text = path, FontSize = 11,
            Foreground = textMid,
            FontFamily = mono,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };

        // Content preview (first 20 lines in a code-style box)
        var previewLines = content.Split('\n');
        var preview = string.Join("\n", previewLines.Take(20));
        if (previewLines.Length > 20) preview += $"\n…  ({previewLines.Length - 20} qator ko'proq)";

        var previewBorder = new Border
        {
            Background      = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
            BorderBrush     = new SolidColorBrush(Color.FromArgb(60, 147, 153, 178)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(10, 6, 10, 6),
            Margin          = new Thickness(0, 0, 0, 10),
            MaxHeight       = 200
        };
        var previewScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 188
        };
        previewScroll.Content = new TextBlock
        {
            Text = preview, FontSize = 11,
            Foreground = textDim,
            FontFamily = mono,
            TextWrapping = TextWrapping.NoWrap
        };
        previewBorder.Child = previewScroll;

        // Buttons
        var allowBtn = MakeButton("✓  Ruxsat berish",
            Color.FromArgb(200, 64, 160, 80), Brushes.White);
        allowBtn.Margin = new Thickness(0, 0, 8, 0);
        allowBtn.Click += (_, _) => { IsHitTestVisible = false; Opacity = 0.55; _tcs.TrySetResult(true); };

        var denyBtn = MakeButton("✕  Rad etish",
            Color.FromArgb(180, 160, 64, 64), Brushes.White);
        denyBtn.Click += (_, _) => { IsHitTestVisible = false; Opacity = 0.55; _tcs.TrySetResult(false); };

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
        btnRow.Children.Add(allowBtn);
        btnRow.Children.Add(denyBtn);

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(pathBlock);
        panel.Children.Add(previewBorder);
        panel.Children.Add(btnRow);
        Child = panel;
    }

    private static Button MakeButton(string text, Color bg, Brush fg) => new()
    {
        Content         = text,
        Height          = 30,
        Padding         = new Thickness(14, 0, 14, 0),
        Background      = new SolidColorBrush(bg),
        Foreground      = fg,
        BorderThickness = new Thickness(0),
        FontSize        = 12,
        Cursor          = Cursors.Hand
    };
}
