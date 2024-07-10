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
        static SortedList<long, List<MidiEvent>> events;

        static void Main(string[] args)
        {
            while (true)
            {
                // Get all MIDI files in the same directory as the executable
                string[] midiFiles = Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.mid");

                // Sort MIDI files alphabetically and display them
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
                        selectedFileIndex--; // Convert to zero-based index
                        break;
                    }
                    Console.WriteLine("Invalid selection, please try again.");
                }

                string midiFilePath = midiFiles[selectedFileIndex];

                try
                {
                    // Load MIDI file
                    Sequence sequence = new Sequence();
                    sequence.Load(midiFilePath);

                    // Initialize MIDI output to loopMIDI virtual port
                    using (OutputDevice outputDevice = new OutputDevice(GetLoopMidiPortNumber("loopMIDI Port")))
                    {
                        Console.WriteLine("Press PageUp to pause/resume. Press Home to fast forward 5 seconds, End to rewind 5 seconds.");

                        Thread playbackThread = new Thread(() => PlayMidiFile(sequence, outputDevice));
                        playbackThread.Start();

                        while (playbackThread.IsAlive)
                        {
                            var key = Console.ReadKey(true).Key;
                            if (key == ConsoleKey.PageUp)
                            {
                                isPaused = !isPaused;
                                if (!isPaused)
                                {
                                    pauseEvent.Set();
                                }
                            }
                            else if (key == ConsoleKey.Home)
                            {
                                FastForward(5000);
                            }
                            else if (key == ConsoleKey.End)
                            {
                                Rewind(5000);
                            }
                        }

                        playbackThread.Join();
                        Console.WriteLine("MIDI playback finished.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }
            }
        }

        private static void PlayMidiFile(Sequence sequence, OutputDevice outputDevice)
        {
            double tempo = 500000; // Default tempo in microseconds per quarter note (120 BPM)
            double microsecondsPerTick = tempo / sequence.Division;
            currentTime = 0;

            events = new SortedList<long, List<MidiEvent>>();

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

            bool sustainPedalDown = false;
            List<ChannelMessage> sustainedNotes = new List<ChannelMessage>();

            foreach (var kvp in events)
            {
                long targetTime = kvp.Key;
                long deltaTime = targetTime - currentTime;
                if (deltaTime < 0) continue; // Ensure deltaTime is non-negative
                currentTime = targetTime;

                if (isPaused)
                {
                    pauseEvent.WaitOne();
                }

                Thread.Sleep((int)(deltaTime * microsecondsPerTick / 1000.0 / playbackSpeed));

                foreach (MidiEvent midiEvent in kvp.Value)
                {
                    if (midiEvent.MidiMessage is ChannelMessage channelMessage)
                    {
                        if (channelMessage.Command == ChannelCommand.NoteOn)
                        {
                            outputDevice.Send(channelMessage);
                        }
                        else if (channelMessage.Command == ChannelCommand.NoteOff)
                        {
                            if (sustainPedalDown)
                            {
                                sustainedNotes.Add(channelMessage);
                            }
                            else
                            {
                                outputDevice.Send(channelMessage);
                            }
                        }
                        else if (channelMessage.Command == ChannelCommand.Controller)
                        {
                            if (channelMessage.Data1 == 64) // 64 is the controller number for the sustain pedal
                            {
                                sustainPedalDown = channelMessage.Data2 >= 64;
                                if (!sustainPedalDown)
                                {
                                    foreach (var sustainedNote in sustainedNotes)
                                    {
                                        outputDevice.Send(sustainedNote);
                                    }
                                    sustainedNotes.Clear();
                                }
                            }
                        }
                    }
                    else if (midiEvent.MidiMessage is MetaMessage metaMessage)
                    {
                        if (metaMessage.MetaType == MetaType.Tempo)
                        {
                            byte[] data = metaMessage.GetBytes();
                            tempo = (data[0] << 16) | (data[1] << 8) | data[2];
                            microsecondsPerTick = tempo / sequence.Division;
                        }
                    }
                }
            }
        }

        private static void FastForward(int milliseconds)
        {
            position += (long)(milliseconds * 1000 / playbackSpeed);
            if (events.ContainsKey(position))
            {
                currentTime = position;
            }
            else
            {
                currentTime = events.Keys[events.Keys.Count - 1];
                position = currentTime;
            }
        }

        private static void Rewind(int milliseconds)
        {
            position -= (long)(milliseconds * 1000 / playbackSpeed);
            if (position < 0)
            {
                position = 0;
            }
            currentTime = position;
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
