using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Midi;

namespace MidiPianoPlayer
{
    class Program
    {
        static volatile bool isPaused = false;
        static AutoResetEvent pauseEvent = new AutoResetEvent(false);
        static double playbackSpeed = 1.0;
        static long position = 0;
        static long currentTime = 0;
        static double tempo = 500000; // Default tempo in microseconds per quarter note (120 BPM)
        static SortedList<long, List<MidiEvent>> events;
        static bool exitToMenu = false;
        static bool isFastForwardingOrRewinding = false;
        static MidiFile midiFile;

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
                        selectedFileIndex--; // Convert to zero-based index
                        break;
                    }
                    Console.WriteLine("Invalid selection, please try again.");
                }

                string midiFilePath = midiFiles[selectedFileIndex];

                try
                {
                    // Load MIDI file
                    midiFile = new MidiFile(midiFilePath, false);

                    // Initialize MIDI output to LoopBe1 virtual port
                    using (var outputDevice = new MidiOut(GetLoopMidiPortNumber("LoopBe Internal MIDI")))
                    {
                        Console.WriteLine("Press PageUp to pause/resume. Press End to fast forward 5 seconds, Home to rewind 5 seconds, Escape to return to menu.");

                        Thread playbackThread = new Thread(() => PlayMidiFile(outputDevice));
                        playbackThread.Start();

                        while (playbackThread.IsAlive)
                        {
                            var key = Console.ReadKey(true).Key;
                            if (key == ConsoleKey.PageUp)
                            {
                                isPaused = !isPaused;
                                // EnqueueDebugMessage($"Pause/Resume at {currentTime / midiFile.DeltaTicksPerQuarterNote * (tempo / 1000000.0):F3} seconds");
                                if (!isPaused)
                                {
                                    pauseEvent.Set();
                                }
                            }
                            else if (key == ConsoleKey.End)
                            {
                                isFastForwardingOrRewinding = true;
                                FastForward(5000);
                                isFastForwardingOrRewinding = false;
                                pauseEvent.Set();
                                // EnqueueDebugMessage($"Fast Forward to {currentTime / midiFile.DeltaTicksPerQuarterNote * (tempo / 1000000.0):F3} seconds");
                            }
                            else if (key == ConsoleKey.Home)
                            {
                                isFastForwardingOrRewinding = true;
                                Rewind(5000);
                                isFastForwardingOrRewinding = false;
                                pauseEvent.Set();
                                // EnqueueDebugMessage($"Rewind to {currentTime / midiFile.DeltaTicksPerQuarterNote * (tempo / 1000000.0):F3} seconds");
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

        private static void PlayMidiFile(MidiOut outputDevice)
        {
            double microsecondsPerTick = tempo / midiFile.DeltaTicksPerQuarterNote;
            currentTime = 0;

            events = new SortedList<long, List<MidiEvent>>();

            foreach (var track in midiFile.Events)
            {
                long trackTime = 0;

                foreach (var midiEvent in track)
                {
                    trackTime += midiEvent.DeltaTime;

                    if (!events.ContainsKey(trackTime))
                    {
                        events[trackTime] = new List<MidiEvent>();
                    }
                    events[trackTime].Add(midiEvent);
                }
            }

            bool sustainPedalDown = false;
            List<MidiEvent> sustainedNotes = new List<MidiEvent>();

            while (!exitToMenu && currentTime < events.Keys.Last())
            {
                if (isPaused)
                {
                    pauseEvent.WaitOne();
                }

                if (isFastForwardingOrRewinding)
                {
                    Thread.Sleep(10); // to prevent busy waiting
                    continue;
                }

                if (events.TryGetValue(currentTime, out var midiEvents))
                {
                    foreach (var midiEvent in midiEvents)
                    {
                        if (midiEvent is NoteOnEvent noteOnEvent)
                        {
                            outputDevice.Send(noteOnEvent.GetAsShortMessage());
                            // EnqueueDebugMessage($"Note On - Note: {noteOnEvent.NoteNumber}, Velocity: {noteOnEvent.Velocity}, Time: {currentTime / midiFile.DeltaTicksPerQuarterNote * (tempo / 1000000.0):F3} seconds");
                        }
                        else if (midiEvent is NoteEvent noteEvent && noteEvent.CommandCode == MidiCommandCode.NoteOff)
                        {
                            if (sustainPedalDown)
                            {
                                sustainedNotes.Add(noteEvent);
                            }
                            else
                            {
                                outputDevice.Send(noteEvent.GetAsShortMessage());
                            }
                            // EnqueueDebugMessage($"Note Off - Note: {noteEvent.NoteNumber}, Velocity: {noteEvent.Velocity}, Time: {currentTime / midiFile.DeltaTicksPerQuarterNote * (tempo / 1000000.0):F3} seconds");
                        }
                        else if (midiEvent is ControlChangeEvent controlChangeEvent && controlChangeEvent.Controller == MidiController.Sustain)
                        {
                            sustainPedalDown = controlChangeEvent.ControllerValue >= 64;
                            if (!sustainPedalDown)
                            {
                                foreach (var sustainedNote in sustainedNotes)
                                {
                                    outputDevice.Send(sustainedNote.GetAsShortMessage());
                                }
                                sustainedNotes.Clear();
                            }
                        }
                        else if (midiEvent is TempoEvent tempoEvent)
                        {
                            tempo = tempoEvent.MicrosecondsPerQuarterNote;
                            microsecondsPerTick = tempo / midiFile.DeltaTicksPerQuarterNote;
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
        }

        private static void FastForward(int milliseconds)
        {
            long ticksToAdvance = (long)(milliseconds * midiFile.DeltaTicksPerQuarterNote / (tempo / 1000000.0));
            position += ticksToAdvance;
            if (position >= events.Keys.Last())
            {
                position = events.Keys.Last();
            }
            currentTime = position;
            // EnqueueDebugMessage($"Fast Forward to {currentTime}");
        }

        private static void Rewind(int milliseconds)
        {
            long ticksToRewind = (long)(milliseconds * midiFile.DeltaTicksPerQuarterNote / (tempo / 1000000.0));
            position -= ticksToRewind;
            if (position < 0)
            {
                position = 0;
            }
            currentTime = position;
            // EnqueueDebugMessage($"Rewind to {currentTime}");
        }

        private static int GetLoopMidiPortNumber(string portName)
        {
            for (int i = 0; i < MidiOut.NumberOfDevices; i++)
            {
                var caps = MidiOut.DeviceInfo(i);
                if (caps.ProductName == portName)
                {
                    return i;
                }
            }
            throw new Exception($"Virtual MIDI port '{portName}' not found.");
        }
    }
}
