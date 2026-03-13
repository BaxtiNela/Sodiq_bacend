using System.Windows;
using System.Windows.Input;

namespace WA.App.Views;

public partial class MiniWidget : Window
{
    private readonly MainWindow _main;

    public MiniWidget(MainWindow main)
    {
        InitializeComponent();
        _main = main;

        // Ekranning pastki-o'ng burchagiga joylashtirish
        var screen = SystemParameters.WorkArea;
        Left = screen.Right - Width - 16;
        Top  = screen.Bottom - Height - 16;

        // Sudrab ko'chirish
        MouseLeftButtonDown += (s, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        };
    }

    private void Icon_Click(object sender, MouseButtonEventArgs e) =>
        _main.RestoreFromTray();

    private void Mic_Click(object sender, RoutedEventArgs e) =>
        _main.StartVoiceFromWidget();

    private void Close_Click(object sender, RoutedEventArgs e) =>
        Close(); // faqat widgetni yopadi, ilova tray da qoladi

    public void SetListening(bool listening)
    {
        StatusIcon.Text  = listening ? "🔴" : "🤖";
        StatusLabel.Text = listening ? "Yozib olinyapti..." : "Tinglayapman...";
        MicBtn.Content   = listening ? "⏹" : "🎤";
    }
}
