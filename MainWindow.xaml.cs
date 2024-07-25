using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NHotkey;
using NHotkey.Wpf;

namespace WpfApp1
{
    public partial class MainWindow : Window
    {
        private MidiPlayer midiPlayer;

        public MainWindow()
        {
            InitializeComponent();
            midiPlayer = new MidiPlayer(this);
            RegisterHotkeys();
            LoadMidiFiles();
        }

        private void RegisterHotkeys()
        {
            HotkeyManager.Current.AddOrReplace("PauseResume", Key.Delete, ModifierKeys.None, midiPlayer.HotkeyPressed);
            HotkeyManager.Current.AddOrReplace("FastForward", Key.End, ModifierKeys.None, midiPlayer.HotkeyPressed);
            HotkeyManager.Current.AddOrReplace("Rewind", Key.Home, ModifierKeys.None, midiPlayer.HotkeyPressed);
            HotkeyManager.Current.AddOrReplace("IncreaseSpeed", Key.PageUp, ModifierKeys.None, midiPlayer.HotkeyPressed);
            HotkeyManager.Current.AddOrReplace("DecreaseSpeed", Key.PageDown, ModifierKeys.None, midiPlayer.HotkeyPressed);
            HotkeyManager.Current.AddOrReplace("ToggleLegitMode", Key.Insert, ModifierKeys.None, midiPlayer.HotkeyPressed);
            HotkeyManager.Current.AddOrReplace("EndSong", Key.Escape, ModifierKeys.None, midiPlayer.HotkeyPressed);
            HotkeyManager.Current.AddOrReplace("DisableSustain", Key.Multiply, ModifierKeys.None, midiPlayer.HotkeyPressed);
        }

        private void LoadMidiFiles()
        {
            string[] midiFiles = Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.mid");
            Array.Sort(midiFiles);
            SongListBox.ItemsSource = midiFiles;
        }

        private void PlaySelectedSongButton_Click(object sender, RoutedEventArgs e)
        {
            if (SongListBox.SelectedItem != null)
            {
                string selectedFile = SongListBox.SelectedItem.ToString();
                midiPlayer.PlayMidiFile(selectedFile);
            }
        }

        private void PauseResumeButton_Click(object sender, RoutedEventArgs e)
        {
            midiPlayer.PauseResume();
        }

        private void FastForwardButton_Click(object sender, RoutedEventArgs e)
        {
            midiPlayer.FastForward();
        }

        private void RewindButton_Click(object sender, RoutedEventArgs e)
        {
            midiPlayer.Rewind();
        }

        private void IncreaseSpeedButton_Click(object sender, RoutedEventArgs e)
        {
            midiPlayer.IncreaseSpeed();
        }

        private void DecreaseSpeedButton_Click(object sender, RoutedEventArgs e)
        {
            midiPlayer.DecreaseSpeed();
        }

        private void ToggleLegitModeButton_Click(object sender, RoutedEventArgs e)
        {
            midiPlayer.ToggleLegitMode();
        }

        private void DisableSustainButton_Click(object sender, RoutedEventArgs e)
        {
            midiPlayer.DisableSustain();
        }

        private void EndSongButton_Click(object sender, RoutedEventArgs e)
        {
            midiPlayer.EndSong();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            Application.Current.Shutdown();
        }
    }
}
