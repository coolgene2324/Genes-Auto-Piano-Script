using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

            // Initialize the MIDI files list
            InitializeMidiFilesList();

            // Register hotkeys
            RegisterHotkeys();

            // Add event handlers
            MidiFilesListBox.SelectionChanged += MidiFilesListBox_SelectionChanged;
        }

        private void InitializeMidiFilesList()
        {
            string midiFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "midi");

            if (!Directory.Exists(midiFolderPath))
            {
                Directory.CreateDirectory(midiFolderPath);
            }

            var midiFiles = Directory.GetFiles(midiFolderPath, "*.mid");
            foreach (var file in midiFiles)
            {
                MidiFilesListBox.Items.Add(Path.GetFileName(file));
            }
        }

        private void RegisterHotkeys()
        {
            HotkeyManager.Current.AddOrReplace("PauseResume", Key.P, ModifierKeys.Control, HotkeyPressed);
            HotkeyManager.Current.AddOrReplace("FastForward", Key.F, ModifierKeys.Control, HotkeyPressed);
            HotkeyManager.Current.AddOrReplace("Rewind", Key.R, ModifierKeys.Control, HotkeyPressed);
            HotkeyManager.Current.AddOrReplace("IncreaseSpeed", Key.I, ModifierKeys.Control, HotkeyPressed);
            HotkeyManager.Current.AddOrReplace("DecreaseSpeed", Key.D, ModifierKeys.Control, HotkeyPressed);
            HotkeyManager.Current.AddOrReplace("ToggleLegitMode", Key.L, ModifierKeys.Control, HotkeyPressed);
            HotkeyManager.Current.AddOrReplace("EndSong", Key.E, ModifierKeys.Control, HotkeyPressed);
            HotkeyManager.Current.AddOrReplace("DisableSustain", Key.S, ModifierKeys.Control, HotkeyPressed);
        }

        private void HotkeyPressed(object sender, HotkeyEventArgs e)
        {
            midiPlayer.HotkeyPressed(sender, e);
        }

        private void MidiFilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Prevent automatic song play on selection change
        }

        private void PlaySelectedSong()
        {
            if (MidiFilesListBox.SelectedItem == null) return;

            string selectedMidiFile = MidiFilesListBox.SelectedItem.ToString();
            string midiFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "midi");
            string midiFilePath = Path.Combine(midiFolderPath, selectedMidiFile);

            midiPlayer.PlayMidiFile(midiFilePath);
        }

        private void PlaySelectedSongButton_Click(object sender, RoutedEventArgs e)
        {
            PlaySelectedSong();
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

        private void EndSongButton_Click(object sender, RoutedEventArgs e)
        {
            midiPlayer.EndSong();
        }

        private void DisableSustainButton_Click(object sender, RoutedEventArgs e)
        {
            midiPlayer.DisableSustain();
        }
    }
}
