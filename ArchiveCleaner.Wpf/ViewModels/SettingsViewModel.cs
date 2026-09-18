using System;
using System.Windows;
using System.Windows.Input;
using ArchiveCleaner.Wpf.Models;
using ArchiveCleaner.Wpf.Services;

namespace ArchiveCleaner.Wpf.ViewModels;

public class SettingsViewModel : ObservableObject
{
    private readonly SettingsManager _settingsManager;
    private readonly Window _window;
    private Key _horizontalAssistKey;
    private Key _verticalAssistKey;
    private string _horizontalKeyDisplay = string.Empty;
    private string _verticalKeyDisplay = string.Empty;

    public Key HorizontalAssistKey
    {
        get => _horizontalAssistKey;
        private set
        {
            if (SetProperty(ref _horizontalAssistKey, value))
            {
                HorizontalKeyDisplay = GetKeyDisplayName(value);
            }
        }
    }

    public Key VerticalAssistKey
    {
        get => _verticalAssistKey;
        private set
        {
            if (SetProperty(ref _verticalAssistKey, value))
            {
                VerticalKeyDisplay = GetKeyDisplayName(value);
            }
        }
    }

    public string HorizontalKeyDisplay
    {
        get => _horizontalKeyDisplay;
        private set => SetProperty(ref _horizontalKeyDisplay, value);
    }

    public string VerticalKeyDisplay
    {
        get => _verticalKeyDisplay;
        private set => SetProperty(ref _verticalKeyDisplay, value);
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ClearHorizontalKeyCommand { get; }
    public ICommand ClearVerticalKeyCommand { get; }

    public SettingsViewModel(SettingsManager settingsManager, Window window)
    {
        _settingsManager = settingsManager;
        _window = window;

        var settings = _settingsManager.Load();
        HorizontalAssistKey = settings.HorizontalAssistKey;
        VerticalAssistKey = settings.VerticalAssistKey;

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => _window.DialogResult = false);
        ClearHorizontalKeyCommand = new RelayCommand(() => SetHorizontalKey(Key.None));
        ClearVerticalKeyCommand = new RelayCommand(() => SetVerticalKey(Key.None));
    }

    public void SetHorizontalKey(Key key)
    {
        if (key == VerticalAssistKey && key != Key.None)
        {
            MessageBox.Show("水平和垂直辅助键不能相同", "设置冲突",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        HorizontalAssistKey = key;
    }

    public void SetVerticalKey(Key key)
    {
        if (key == HorizontalAssistKey && key != Key.None)
        {
            MessageBox.Show("水平和垂直辅助键不能相同", "设置冲突",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        VerticalAssistKey = key;
    }

    private void Save()
    {
        var settings = new AppSettings
        {
            HorizontalAssistKey = HorizontalAssistKey,
            VerticalAssistKey = VerticalAssistKey
        };
        _settingsManager.Save(settings);
        _window.DialogResult = true;
    }

    private static string GetKeyDisplayName(Key key)
    {
        if (key == Key.None) return "未设置";

        return key switch
        {
            Key.LeftCtrl => "Left Ctrl",
            Key.RightCtrl => "Right Ctrl",
            Key.LeftShift => "Left Shift",
            Key.RightShift => "Right Shift",
            Key.LeftAlt => "Left Alt",
            Key.RightAlt => "Right Alt",
            Key.Space => "空格",
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            Key.Escape => "Esc",
            _ => key.ToString()
        };
    }
}
