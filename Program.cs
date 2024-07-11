using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms; // Will remove after debugging
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
        static double tempo = 500000; // Default tempo in microseconds per quarter note (120 BPM)
        static SortedList<long, List<MidiEvent>> events;
        static bool exitToMenu = false;
        static bool isFastForwardingOrRewinding = false;
        static Sequence sequence;
        static DebugForm debugForm;

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            debugForm = new DebugForm();
            Thread debugThread = new Thread(() => Application.Run(debugForm));
            debugThread.Start();

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
                    sequence = new Sequence();
                    sequence.Load(midiFilePath);

                    // Initialize MIDI output to loopMIDI virtual port
                    using (OutputDevice outputDevice = new OutputDevice(GetLoopMidiPortNumber("loopMIDI Port")))
                    {
                        Console.WriteLine("Press PageUp to pause/resume. Press End to fast forward 5 seconds, Home to rewind 5 seconds, Escape to return to menu.");

                        Thread playbackThread = new Thread(() => PlayMidiFile(outputDevice));
                        playbackThread.Start();

                        while (playbackThread.IsAlive) // nah i am pretty dead
                        {
                            var key = Console.ReadKey(true).Key;
                            if (key == ConsoleKey.PageUp)
                            {
                                isPaused = !isPaused;
                                debugForm.AddMessage($"Pause/Resume at {currentTime / sequence.Division * (tempo / 1000000.0):F3} seconds");
                                if (!isPaused)
                                {
                                    pauseEvent.Set();
                                }
                            }
                            else if (key == ConsoleKey.End)
                            {
                                isFastForwardingOrRewinding = true; // pain in the ass still don't work
                                FastForward(5000);
                                isFastForwardingOrRewinding = false;
                                pauseEvent.Set();
                                debugForm.AddMessage($"Fast Forward to {currentTime / sequence.Division * (tempo / 1000000.0):F3} seconds");
                            }
                            else if (key == ConsoleKey.Home)
                            {
                                isFastForwardingOrRewinding = true;
                                Rewind(5000);
                                isFastForwardingOrRewinding = false;
                                pauseEvent.Set();
                                debugForm.AddMessage($"Rewind to {currentTime / sequence.Division * (tempo / 1000000.0):F3} seconds");
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

        private static void PlayMidiFile(OutputDevice outputDevice)
        {
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
                    foreach (MidiEvent midiEvent in midiEvents)
                    {
                        if (midiEvent.MidiMessage is ChannelMessage channelMessage)
                        {
                            if (channelMessage.Command == ChannelCommand.NoteOn || channelMessage.Command == ChannelCommand.NoteOff)
                            {
                                outputDevice.Send(channelMessage);
                                debugForm.AddMessage($"Note {(channelMessage.Command == ChannelCommand.NoteOn ? "On" : "Off")} - Note: {channelMessage.Data1}, Velocity: {channelMessage.Data2}, Time: {currentTime / sequence.Division * (tempo / 1000000.0):F3} seconds");
                                if (channelMessage.Command == ChannelCommand.NoteOff && sustainPedalDown)
                                {
                                    sustainedNotes.Add(channelMessage);
                                }
                            }
                            else if (channelMessage.Command == ChannelCommand.Controller && channelMessage.Data1 == 64)
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
                        else if (midiEvent.MidiMessage is MetaMessage)
                        {
                            if (((MetaMessage)midiEvent.MidiMessage).MetaType == MetaType.Tempo)
                            {
                                byte[] data = ((MetaMessage)midiEvent.MidiMessage).GetBytes();
                                tempo = (data[0] << 16) | (data[1] << 8) | data[2];
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

                Thread.Sleep((int)(deltaTime * microsecondsPerTick / 1000.0 / playbackSpeed));
            }
        }

        private static void FastForward(int milliseconds)
        {
            long ticksToAdvance = (long)(milliseconds * sequence.Division / (tempo / 1000000.0));
            position += ticksToAdvance;
            if (position >= events.Keys.Last())
            {
                position = events.Keys.Last();
            }
            currentTime = position;
        }

        private static void Rewind(int milliseconds)
        {
            long ticksToRewind = (long)(milliseconds * sequence.Division / (tempo / 1000000.0));
            position -= ticksToRewind;
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

    public class DebugForm : Form
    {
        private ListBox listBox;

        public DebugForm()
        {
            Text = "Debug Information";
            Width = 800;
            Height = 600;

            listBox = new ListBox() { Dock = DockStyle.Fill };
            Controls.Add(listBox);
        }

        public void AddMessage(string message)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<string>(AddMessage), message);
            }
            else
            {
                listBox.Items.Add(message);
                listBox.SelectedIndex = listBox.Items.Count - 1;
            }
        }
    }
}
