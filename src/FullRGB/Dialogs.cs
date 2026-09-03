using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Colors = System.Windows.Media.Colors;
using FontFamily = System.Windows.Media.FontFamily;
using FlowDirection = System.Windows.FlowDirection;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MouseButtonState = System.Windows.Input.MouseButtonState;

namespace FullRGB;

/// <summary>
/// Small themed modal dialogs (text prompt + yes/no confirm). WPF's MessageBox is a
/// Win32 grey box that breaks the dark UI, and there is no built-in input box at all.
/// </summary>
public sealed class PromptDialog : Window
{
    private readonly TextBox _input;

    public string Value => _input.Text.Trim();

    public PromptDialog(Window? owner, string title, string? initial, string okText, string cancelText)
    {
        Owner = owner;
        Title = title;
        Width = 340;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Background = Brushes.Transparent;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        FlowDirection = L10n.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _input = new TextBox
        {
            Style = (Style)Application.Current.FindResource("Inp"),
            Text = initial ?? "",
            Margin = new Thickness(0, 0, 0, 14),
        };
        _input.SelectAll();

        var ok = new Button
        {
            Style = (Style)Application.Current.FindResource("AccentBtn"),
            Content = okText,
            IsDefault = true,
        };
        ok.Click += (_, _) => { DialogResult = Value.Length > 0; };

        var cancel = new Button
        {
            Style = (Style)Application.Current.FindResource("GhostBtn"),
            Content = cancelText,
            IsCancel = true,
            Margin = new Thickness(0, 0, 8, 0),
        };
        cancel.Click += (_, _) => { DialogResult = false; };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.FindResource("Txt"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(_input);
        body.Children.Add(buttons);

        Content = new Border
        {
            Background = (Brush)Application.Current.FindResource("BgElevated"),
            BorderBrush = (Brush)Application.Current.FindResource("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(18, 16, 18, 16),
            Margin = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 28, ShadowDepth = 6, Opacity = 0.5, Color = Colors.Black,
            },
            Child = body,
        };

        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    /// <summary>Returns the entered text, or null when cancelled/empty.</summary>
    public static string? Ask(Window? owner, string title, string? initial)
    {
        var dlg = new PromptDialog(owner, title, initial, L10n.T("dlg.ok"), L10n.T("dlg.cancel"));
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }
}

/// <summary>Themed yes/no confirmation.</summary>
public static class ConfirmDialog
{
    public static bool Ask(Window? owner, string message, string confirmText, bool danger = false)
    {
        var win = new Window
        {
            Owner = owner,
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Background = Brushes.Transparent,
            AllowsTransparency = true,
            ShowInTaskbar = false,
            FlowDirection = L10n.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
        };

        var ok = new Button
        {
            Style = (Style)Application.Current.FindResource(danger ? "Btn" : "AccentBtn"),
            Content = confirmText,
            IsDefault = true,
        };
        if (danger) ok.Foreground = (Brush)Application.Current.FindResource("Danger");
        ok.Click += (_, _) => win.DialogResult = true;

        var cancel = new Button
        {
            Style = (Style)Application.Current.FindResource("GhostBtn"),
            Content = L10n.T("dlg.cancel"),
            IsCancel = true,
            Margin = new Thickness(0, 0, 8, 0),
        };
        cancel.Click += (_, _) => win.DialogResult = false;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = message,
            Style = (Style)Application.Current.FindResource("Txt"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        });
        body.Children.Add(buttons);

        win.Content = new Border
        {
            Background = (Brush)Application.Current.FindResource("BgElevated"),
            BorderBrush = (Brush)Application.Current.FindResource("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(18, 16, 18, 16),
            Margin = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 28, ShadowDepth = 6, Opacity = 0.5, Color = Colors.Black,
            },
            Child = body,
        };
        win.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
        return win.ShowDialog() == true;
    }
}
