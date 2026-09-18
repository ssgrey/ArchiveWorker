using System.Windows;
using System.Windows.Input;
using ArchiveCleaner.Wpf.ViewModels;

namespace ArchiveCleaner.Wpf.Views;

public partial class SettingsWindow : Window
{
    private SettingsViewModel ViewModel => (SettingsViewModel)DataContext;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OnHorizontalKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = GetActualKey(e);

        if (key == Key.None)
            return;

        ViewModel.SetHorizontalKey(key);
        HorizontalKeyTextBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private void OnVerticalKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = GetActualKey(e);

        if (key == Key.None)
            return;

        ViewModel.SetVerticalKey(key);
        VerticalKeyTextBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private static Key GetActualKey(KeyEventArgs e)
    {
        // Handle system keys (Alt combinations)
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Handle ImeProcessed and other special cases
        if (key == Key.ImeProcessed)
            key = e.ImeProcessedKey;

        // For modifier keys, detect which one is actually pressed
        if (key == Key.LeftCtrl || key == Key.RightCtrl)
            return key;
        if (key == Key.LeftShift || key == Key.RightShift)
            return key;
        if (key == Key.LeftAlt || key == Key.RightAlt)
            return key;

        // Ignore standalone modifier key presses without a real key
        if (Keyboard.Modifiers != ModifierKeys.None && IsModifierOnly(key))
            return Key.None;

        return key;
    }

    private static bool IsModifierOnly(Key key)
    {
        return key == Key.LeftCtrl || key == Key.RightCtrl ||
               key == Key.LeftShift || key == Key.RightShift ||
               key == Key.LeftAlt || key == Key.RightAlt ||
               key == Key.LWin || key == Key.RWin;
    }

    private void OnHorizontalKeyBoxFocus(object sender, RoutedEventArgs e)
    {
        HorizontalKeyTextBox.SelectAll();
    }

    private void OnVerticalKeyBoxFocus(object sender, RoutedEventArgs e)
    {
        VerticalKeyTextBox.SelectAll();
    }
}
