using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using Sanford.Multimedia.Midi;
using NHotkey;
using NHotkey.Wpf;

namespace WpfApp1
{
    public class MidiPlayer
    {
        private MainWindow mainWindow;
        private bool isPaused = false;
        private AutoResetEvent pauseEvent = new AutoResetEvent(false);
        private double playbackSpeed = 1.0;
        private long position = 0;
        private long currentTime = 0;
        private double tempo = 500000; // Default tempo
        private SortedList<long, List<MidiEvent>> events;
        private bool exitToMenu = false;
        private bool isFastForwardingOrRewinding = false;
        private bool isLegitMode = false; // New variable for legit mode
        private Random random = new Random(); // New random instance
        private Sequence sequence;
        private Thread playbackThread;
        private OutputDevice outputDevice;
        private Dictionary<int, List<ChannelMessage>> activeNotes;
        private bool sustainPedalDown = false; // Track sustain pedal state
        private readonly object speedLock = new object(); // Lock object for thread safety

        public MidiPlayer(MainWindow mainWindow)
        {
            this.mainWindow = mainWindow;
        }

        public void PlayMidiFile(string midiFilePath)
        {
            try
            {
                // Clean up previous playback thread and reset flags
                exitToMenu = false;
                isPaused = false;
                pauseEvent.Set(); // Release any previous pause

                if (playbackThread != null && playbackThread.IsAlive)
                {
                    playbackThread.Join(); // Wait for previous thread to terminate
                }

                // Load MIDI file
                sequence = new Sequence();
                sequence.Load(midiFilePath);

                // Initialize MIDI output to loopMIDI virtual port
                outputDevice = new OutputDevice(GetLoopMidiPortNumber("loopMIDI Port"));
                mainWindow.Dispatcher.Invoke(() =>
                {
                    mainWindow.StatusTextBlock.Text = "Playing: " + Path.GetFileName(midiFilePath);
                    mainWindow.PlaybackSpeedTextBlock.Text = $"Playback Speed: {playbackSpeed:F2}x";
                    mainWindow.CurrentTimeTextBlock.Text = $"0:00 / {GetFormattedTime(sequence.GetLength())}";
                });

                playbackThread = new Thread(() => PlayMidiFileThread());
                playbackThread.Start();
            }
            catch (Exception ex)
            {
                mainWindow.Dispatcher.Invoke(() =>
                {
                    mainWindow.StatusTextBlock.Text = $"Error: {ex.Message}";
                });
            }
        }

        private string GetFormattedTime(long ticks)
        {
            TimeSpan timeSpan = TimeSpan.FromSeconds(ticks / (sequence.Division * (1000000.0 / tempo)));
            return $"{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}";
        }

        public void PauseResume()
        {
            isPaused = !isPaused;
            mainWindow.Dispatcher.Invoke(() =>
            {
                mainWindow.StatusTextBlock.Text = isPaused ? "Paused" : "Resumed";
            });
            if (!isPaused)
            {
                pauseEvent.Set();
            }
        }

        public void FastForward()
        {
            isFastForwardingOrRewinding = true;
            long previousTime = currentTime;
            FastForward(5000);
            isFastForwardingOrRewinding = false;
            pauseEvent.Set();
            mainWindow.Dispatcher.Invoke(() =>
            {
                mainWindow.StatusTextBlock.Text = $"Fast Forward from {previousTime / sequence.Division * (tempo / 1000000.0):F3} seconds to {currentTime / sequence.Division * (tempo / 1000000.0):F3} seconds";
            });
        }

        public void Rewind()
        {
            isFastForwardingOrRewinding = true;
            long previousTime = currentTime;
            Rewind(5000);
            isFastForwardingOrRewinding = false;
            pauseEvent.Set();
            mainWindow.Dispatcher.Invoke(() =>
            {
                mainWindow.StatusTextBlock.Text = $"Rewind from {previousTime / sequence.Division * (tempo / 1000000.0):F3} seconds to {currentTime / sequence.Division * (tempo / 1000000.0):F3} seconds";
            });
        }

        public void IncreaseSpeed()
        {
            lock (speedLock)
            {
                playbackSpeed *= 1.1;
                mainWindow.Dispatcher.Invoke(() =>
                {
                    mainWindow.PlaybackSpeedTextBlock.Text = $"Playback Speed: {playbackSpeed:F2}x";
                });
            }
        }

        public void DecreaseSpeed()
        {
            lock (speedLock)
            {
                playbackSpeed /= 1.1;
                mainWindow.Dispatcher.Invoke(() =>
                {
                    mainWindow.PlaybackSpeedTextBlock.Text = $"Playback Speed: {playbackSpeed:F2}x";
                });
            }
        }

        public void ToggleLegitMode()
        {
            isLegitMode = !isLegitMode;
            mainWindow.Dispatcher.Invoke(() =>
            {
                mainWindow.StatusTextBlock.Text = isLegitMode ? "Legit mode enabled" : "Legit mode disabled";
            });
        }

        public void ExitToMenu()
        {
            exitToMenu = true;
            if (isPaused)
            {
                pauseEvent.Set();
            }
            mainWindow.Dispatcher.Invoke(() =>
            {
                mainWindow.StatusTextBlock.Text = "Exited to menu";
            });
        }

        public void DisableSustain()
        {
            sustainPedalDown = false;
            var sustainMessage = new ChannelMessage(ChannelCommand.Controller, 0, 64, 0);
            outputDevice.Send(sustainMessage);
            mainWindow.Dispatcher.Invoke(() =>
            {
                mainWindow.StatusTextBlock.Text = "Sustain pedal disabled";
            });
        }

        private void PlayMidiFileThread()
        {
            double microsecondsPerTick = tempo / sequence.Division;
            currentTime = 0;

            events = new SortedList<long, List<MidiEvent>>();

            // Initialize activeNotes here
            activeNotes = new Dictionary<int, List<ChannelMessage>>();

            foreach (Track track in sequence)
            {
                long trackTime = 0;

                foreach (MidiEvent midiEvent in track.Iterator())
                {
                    trackTime += midiEvent.DeltaTicks;

                    if (!events.ContainsKey(trackTime))
                    {
                        events[trackTime] = new List<MidiEvent>();
                    }
                    events[trackTime].Add(midiEvent);
                }
            }

            while (currentTime < events.Keys.Last())
            {
                if (isPaused)
                {
                    // Pause: Send Note Off for all active notes
                    TurnOffAllNotes();
                    pauseEvent.WaitOne();
                    continue;
                }

                if (isFastForwardingOrRewinding)
                {
                    // Fast Forward or Rewind: Send Note Off for all active notes
                    TurnOffAllNotes();

                    if (isFastForwardingOrRewinding)
                    {
                        Thread.Sleep(10); // to prevent busy waiting
                        continue;
                    }
                }

                if (exitToMenu)
                {
                    // If exit to menu, break the playback loop
                    TurnOffAllNotes();
                    break;
                }

                if (events.TryGetValue(currentTime, out var midiEvents))
                {
                    foreach (MidiEvent midiEvent in midiEvents)
                    {
                        if (midiEvent.MidiMessage is ChannelMessage channelMessage)
                        {
                            int channel = channelMessage.MidiChannel;

                            if (!IsPercussion(channelMessage))
                            {
                                if (channelMessage.Command == ChannelCommand.NoteOn && channelMessage.Data2 > 0)
                                {
                                    // Check if there's already an active note on this channel
                                    if (activeNotes.ContainsKey(channel))
                                    {
                                        // Check if the note is currently playing (data2 > 0 means Note On)
                                        foreach (var note in activeNotes[channel].ToList())
                                        {
                                            if (note.Data1 == channelMessage.Data1)
                                            {
                                                // Send a Note Off for the current note to prevent stacking
                                                outputDevice.Send(new ChannelMessage(ChannelCommand.NoteOff, note.MidiChannel, note.Data1, note.Data2));
                                                activeNotes[channel].Remove(note);

                                                // Ensure there's a gap before sending the next Note On
                                                if (isLegitMode)
                                                {
                                                    Thread.Sleep(random.Next(5, 50));
                                                }
                                            }
                                        }
                                    }

                                    // Introduce random delay in legit mode
                                    if (isLegitMode)
                                    {
                                        Thread.Sleep(random.Next(5, 50));
                                    }

                                    // Send the new Note On message
                                    outputDevice.Send(channelMessage);

                                    // Add the note to active notes
                                    if (!activeNotes.ContainsKey(channel))
                                    {
                                        activeNotes[channel] = new List<ChannelMessage>();
                                    }
                                    activeNotes[channel].Add(channelMessage);
                                }
                                else if (channelMessage.Command == ChannelCommand.NoteOff || (channelMessage.Command == ChannelCommand.NoteOn && channelMessage.Data2 == 0))
                                {
                                    // Remove the note from active notes when it ends
                                    if (activeNotes.ContainsKey(channel))
                                    {
                                        var noteToRemove = activeNotes[channel].FirstOrDefault(n => n.Data1 == channelMessage.Data1);
                                        if (noteToRemove != null)
                                        {
                                            outputDevice.Send(channelMessage);
                                            activeNotes[channel].Remove(noteToRemove);
                                        }
                                    }
                                }
                                else if (channelMessage.Command == ChannelCommand.Controller && channelMessage.Data1 == 64)
                                {
                                    HandleSustainPedal(channelMessage, channel);
                                }
                            }
                        }
                        else if (midiEvent.MidiMessage is MetaMessage)
                        {
                            // Handle tempo changes if needed
                            MetaMessage metaMessage = midiEvent.MidiMessage as MetaMessage;
                            if (metaMessage.MetaType == MetaType.Tempo)
                            {
                                byte[] data = metaMessage.GetBytes(); // Use GetBytes() method instead of GetData()
                                tempo = (data[0] << 16) | (data[1] << 8) | data[2];
                                mainWindow.Dispatcher.Invoke(() =>
                                {
                                    mainWindow.TempoTextBlock.Text = $"Tempo: {60000000 / tempo} BPM";
                                });
                                if (tempo == 0)
                                {
                                    tempo = 500000; // Reset to default tempo if tempo is zero
                                    mainWindow.Dispatcher.Invoke(() =>
                                    {
                                        mainWindow.TempoTextBlock.Text = "Tempo: 120 BPM (Default)";
                                    });
                                }
                                microsecondsPerTick = tempo / sequence.Division;
                            }
                        }
                    }
                }

                long nextEventTime = currentTime;
                if (events.Keys.Count > 0)
                {
                    nextEventTime = events.Keys.FirstOrDefault(t => t > currentTime);
                }

                long deltaTime = nextEventTime - currentTime;
                currentTime = nextEventTime;

                position = currentTime;
                Thread.Sleep((int)(deltaTime * microsecondsPerTick / 1000.0 / playbackSpeed));

                // Update current time
                mainWindow.Dispatcher.Invoke(() =>
                {
                    mainWindow.CurrentTimeTextBlock.Text = $"{GetFormattedTime(currentTime)} / {GetFormattedTime(sequence.GetLength())}";
                });
            }

            // Ensure all notes are turned off when playback ends
            TurnOffAllNotes();

            // Signal the main thread to return to the menu
            exitToMenu = true;
            pauseEvent.Set();
        }

        private void HandleSustainPedal(ChannelMessage channelMessage, int channel)
        {
            // Sustain pedal logic
            if (channelMessage.Data2 >= 64)
            {
                // Sustain pedal pressed
                if (!sustainPedalDown)
                {
                    sustainPedalDown = true;
                    var sustainMessage = new ChannelMessage(ChannelCommand.Controller, channel, 64, 127);
                    outputDevice.Send(sustainMessage);
                }
            }
            else
            {
                // Sustain pedal released
                if (sustainPedalDown)
                {
                    sustainPedalDown = false;
                    var sustainMessage = new ChannelMessage(ChannelCommand.Controller, channel, 64, 0);
                    outputDevice.Send(sustainMessage);
                }
            }
        }

        private void FastForward(int milliseconds)
        {
            double secondsToAdvance = milliseconds / 1000.0;
            long ticksToAdvance = (long)(secondsToAdvance * sequence.Division * (1000000.0 / tempo));
            long newPosition = position + ticksToAdvance;

            if (newPosition >= events.Keys.Last())
            {
                newPosition = events.Keys.Last() - 1; // Prevents jumping to the end
            }

            // Turn off notes before advancing
            TurnOffAllNotes();

            position = newPosition;
            currentTime = position;
        }

        private void Rewind(int milliseconds)
        {
            double secondsToRewind = milliseconds / 1000.0;
            long ticksToRewind = (long)(secondsToRewind * sequence.Division * (1000000.0 / tempo));
            long newPosition = position - ticksToRewind;

            if (newPosition < 0)
            {
                newPosition = 0;
            }

            // Turn off notes before rewinding
            TurnOffAllNotes();

            position = newPosition;
            currentTime = position;
        }

        private void TurnOffAllNotes()
        {
            foreach (var kvp in activeNotes)
            {
                foreach (var note in kvp.Value)
                {
                    outputDevice.Send(new ChannelMessage(ChannelCommand.NoteOff, note.MidiChannel, note.Data1, note.Data2));
                }
                kvp.Value.Clear();
            }
        }

        private bool IsPercussion(ChannelMessage channelMessage)
        {
            // Check if the MIDI channel is for percussion
            return channelMessage.MidiChannel >= 9 && channelMessage.MidiChannel <= 16;
        }

        private int GetLoopMidiPortNumber(string portName)
        {
            for (int i = 0; i < OutputDevice.DeviceCount; i++)
            {
                MidiOutCaps caps = OutputDevice.GetDeviceCapabilities(i);
                if (caps.name == portName)
                {
                    return i;
                }
            }
            throw new Exception($"Virtual MIDI port '{portName}' not found.");
        }

        public void HotkeyPressed(object sender, HotkeyEventArgs e)
        {
            switch (e.Name)
            {
                case "PauseResume":
                    PauseResume();
                    break;
                case "FastForward":
                    FastForward();
                    break;
                case "Rewind":
                    Rewind();
                    break;
                case "IncreaseSpeed":
                    IncreaseSpeed();
                    break;
                case "DecreaseSpeed":
                    DecreaseSpeed();
                    break;
                case "ToggleLegitMode":
                    ToggleLegitMode();
                    break;
                case "ExitToMenu":
                    ExitToMenu();
                    break;
                case "DisableSustain":
                    DisableSustain();
                    break;
            }
        }
    }
}
