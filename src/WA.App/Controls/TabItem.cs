using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WA.App.Controls;

/// <summary>Yuqori tab tugmasi - VS Code/Warp uslubida</summary>
public class TabItem : Button
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(TabItem));

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(TabItem),
            new PropertyMetadata(false, OnIsActiveChanged));

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    static TabItem()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(TabItem),
            new FrameworkPropertyMetadata(typeof(TabItem)));
    }

    public TabItem()
    {
        UpdateVisuals();
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((TabItem)d).UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        var app = Application.Current;
        if (IsActive)
        {
            Background = app?.FindResource("Brush.BG.Base") as Brush
                         ?? new SolidColorBrush(Color.FromRgb(30, 30, 46));
            Foreground = app?.FindResource("Brush.Text.Primary") as Brush
                         ?? Brushes.White;
        }
        else
        {
            Background = Brushes.Transparent;
            Foreground = app?.FindResource("Brush.Text.Muted") as Brush
                         ?? Brushes.Gray;
        }

        BorderThickness = IsActive
            ? new Thickness(0, 2, 0, 0)
            : new Thickness(0);
        BorderBrush = IsActive
            ? app?.FindResource("Brush.Accent.Blue") as Brush ?? Brushes.CornflowerBlue
            : Brushes.Transparent;

        Padding = new Thickness(14, 4, 14, 4);
        Height = 34;
        FontSize = 12;
        Cursor = System.Windows.Input.Cursors.Hand;
        Content = Header;
    }
}
