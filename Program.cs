using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Sanford.Multimedia.Midi;

namespace MidiPianoPlayer
{
    class Program
    {
        static volatile bool isPaused = false;
        static AutoResetEvent pauseEvent = new AutoResetEvent(false);
        static double playbackSpeed = 1.0;
        static long position = 0;
        static long currentTime = 0;
        static double tempo = 500000; // Default tempo in microsec per quarter note (currently 120 BPM)
        static SortedList<long, List<MidiEvent>> events;
        static bool exitToMenu = false;
        static bool isFastForwardingOrRewinding = false;
        static Sequence sequence;
        static Thread playbackThread;
        static OutputDevice outputDevice;
        static Dictionary<int, List<ChannelMessage>> activeNotes;

        [STAThread]
        static void Main(string[] args)
        {
            while (true)
            {
                // Get all MIDI files in the same directory as the executable
                string[] midiFiles = Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.mid");

                // Sort MIDI files and display them
                Array.Sort(midiFiles);
                Console.WriteLine("Select a MIDI file to play:");
                for (int i = 0; i < midiFiles.Length; i++)
                {
                    Console.WriteLine($"{i + 1}. {Path.GetFileName(midiFiles[i])}");
                }

                // Read user selection
                int selectedFileIndex = 0;
                while (true)
                {
                    Console.Write("Enter the number of the MIDI file: ");
                    if (int.TryParse(Console.ReadLine(), out selectedFileIndex) &&
                        selectedFileIndex > 0 &&
                        selectedFileIndex <= midiFiles.Length)
                    {
                        selectedFileIndex--;
                        break;
                    }
                    Console.WriteLine("Invalid selection, please try again.");
                }

                string midiFilePath = midiFiles[selectedFileIndex];

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

                    // Initialize MIDI output to LoopBe1 virtual port
                    outputDevice = new OutputDevice(GetLoopMidiPortNumber("LoopBe Internal MIDI"));
                    Console.WriteLine("Press PageUp to pause/resume. Press End to fast forward 5 seconds, Home to rewind 5 seconds, Escape to return to menu.");

                    playbackThread = new Thread(() => PlayMidiFile());
                    playbackThread.Start();

                    while (playbackThread.IsAlive)
                    {
                        var key = Console.ReadKey(true).Key;
                        if (key == ConsoleKey.PageUp)
                        {
                            isPaused = !isPaused;
                            Console.WriteLine(isPaused ? "Paused" : "Resumed");
                            if (!isPaused)
                            {
                                pauseEvent.Set();
                            }
                        }
                        else if (key == ConsoleKey.End)
                        {
                            isFastForwardingOrRewinding = true;
                            long previousTime = currentTime;
                            FastForward(5000);
                            isFastForwardingOrRewinding = false;
                            pauseEvent.Set();
                            Console.WriteLine($"Fast Forward from {previousTime / sequence.Division * (tempo / 1000000.0):F3} seconds to {currentTime / sequence.Division * (tempo / 1000000.0):F3} seconds");
                        }
                        else if (key == ConsoleKey.Home)
                        {
                            isFastForwardingOrRewinding = true;
                            long previousTime = currentTime;
                            Rewind(5000);
                            isFastForwardingOrRewinding = false;
                            pauseEvent.Set();
                            Console.WriteLine($"Rewind from {previousTime / sequence.Division * (tempo / 1000000.0):F3} seconds to {currentTime / sequence.Division * (tempo / 1000000.0):F3} seconds");
                        }
                        else if (key == ConsoleKey.Escape)
                        {
                            exitToMenu = true;
                            if (isPaused)
                            {
                                pauseEvent.Set();
                            }
                            break;
                        }
                    }

                    playbackThread.Join(); // Ensure playback thread completes
                    Console.WriteLine("MIDI playback finished.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }
                finally
                {
                    // Close the output device after playback
                    if (outputDevice != null)
                    {
                        outputDevice.Dispose();
                    }
                }
            }
        }

        private static void PlayMidiFile()
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

            while (!exitToMenu && currentTime < events.Keys.Last())
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

                if (events.TryGetValue(currentTime, out var midiEvents))
                {
                    foreach (MidiEvent midiEvent in midiEvents)
                    {
                        if (midiEvent.MidiMessage is ChannelMessage channelMessage)
                        {
                            int channel = channelMessage.MidiChannel;

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
                                        }
                                    }
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
                                // Sustain pedal logic (if needed)
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
                                Console.WriteLine($"Tempo changed to {tempo} microseconds per quarter note");
                                if (tempo == 0)
                                {
                                    tempo = 500000; // Reset to default tempo if tempo is zero
                                    Console.WriteLine("Tempo was zero, reset to default tempo 500000 microseconds per quarter note");
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
            }

            // Ensure all notes are turned off when playback ends
            TurnOffAllNotes();
        }

        private static void FastForward(int milliseconds)
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

            Console.WriteLine($"Fast forwarding {milliseconds} ms (advancing {ticksToAdvance} ticks) from {position} to {newPosition}");
            position = newPosition;
            currentTime = position;
        }

        private static void Rewind(int milliseconds)
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

            Console.WriteLine($"Rewinding {milliseconds} ms (rewinding {ticksToRewind} ticks) from {position} to {newPosition}");
            position = newPosition;
            currentTime = position;
        }

        private static void TurnOffAllNotes()
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

        private static int GetLoopMidiPortNumber(string portName)
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
    }
}
