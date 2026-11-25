using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AudioPlugSharp;
using NAudio.Wave;

namespace AcousticGuitarVst;

public sealed class SampleZone
{
    public int RootMidiNote { get; init; }
    public float[] Samples { get; init; } = Array.Empty<float>();
    public int SampleRate { get; init; }
}

public sealed class StringVoice
{
    public bool IsActive { get; set; }
    public bool IsReleasing { get; set; }
    public int MidiNote { get; set; }
    public SampleZone? Zone { get; set; }
    public double Playhead { get; set; }
    public double StepPerSample { get; set; }
    public float Gain { get; set; }
    public float Envelope { get; set; }
}

public sealed class AcousticGuitarPlugin : AudioPluginBase
{
    private const int StringCount = 6;
    private FloatAudioIOPort? _stereoOutput;

    private readonly List<SampleZone> _zones = new();
    private readonly StringVoice[] _voices = new StringVoice[StringCount];
    private readonly int[] _voiceOrder = new int[StringCount];

    public AcousticGuitarPlugin()
    {
        PluginName = "CSharp 6-String Acoustic";
        PluginVersion = "1.0.0";
        PluginID = 0x4755495441523653;
        SampleFormatsSupported = EAudioBitsPerSample.Bits32;

        for (int i = 0; i < StringCount; i++)
        {
            _voices[i] = new StringVoice();
            _voiceOrder[i] = i;
        }
    }

    public override void Initialize()
    {
        base.Initialize();

        OutputPorts = new AudioIOPort[]
        {
            _stereoOutput = new FloatAudioIOPort("Stereo Out", EAudioChannelConfiguration.Stereo)
        };

        AddParameter(new DecibelParameter
        {
            ID = "master_gain",
            Name = "Master Gain",
            MinValue = -48.0,
            MaxValue = 12.0,
            DefaultValue = 0.0
        });

        AddParameter(new AudioPluginParameter
        {
            ID = "release_rate",
            Name = "Damping",
            MinValue = 0.0002,
            MaxValue = 0.01,
            DefaultValue = 0.0015
        });

        HookMidiEvents();
        LoadGuitarSamples();
    }

    private void HookMidiEvents()
    {
        var midiEventInfo = Host.GetType().GetEvent("MidiReceived")
            ?? Host.GetType().GetEvent("OnMidiReceived");

        if (midiEventInfo?.EventHandlerType is not null)
        {
            var handler = Delegate.CreateDelegate(
                midiEventInfo.EventHandlerType,
                this,
                typeof(AcousticGuitarPlugin).GetMethod(
                    nameof(HandleRawMidiEvent),
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!);
            midiEventInfo.AddEventHandler(Host, handler);
        }
    }

    private void HandleRawMidiEvent(params object?[] args)
    {
        byte status = 0;
        byte data1 = 0;
        byte data2 = 0;

        foreach (var arg in args)
        {
            if (arg is null) continue;
            var t = arg.GetType();
            var statusProp = t.GetProperty("Status") ?? t.GetProperty("Command") ?? t.GetProperty("Message");
            var noteProp = t.GetProperty("Note") ?? t.GetProperty("Data1") ?? t.GetProperty("NoteNumber");
            var velProp = t.GetProperty("Velocity") ?? t.GetProperty("Data2");

            if (statusProp is not null && noteProp is not null && velProp is not null)
            {
                status = Convert.ToByte(statusProp.GetValue(arg));
                data1 = Convert.ToByte(noteProp.GetValue(arg));
                data2 = Convert.ToByte(velProp.GetValue(arg));
                break;
            }
        }

        int command = status & 0xF0;
        if (command == 0x90)
        {
            TriggerNoteOn(data1, data2);
        }
        else if (command == 0x80)
        {
            TriggerNoteOff(data1);
        }
    }

    private void LoadGuitarSamples()
    {
        _zones.Clear();

        string samplesDir = Path.Combine(AppContext.BaseDirectory, "Samples");
        if (!Directory.Exists(samplesDir))
        {
            return;
        }

        foreach (string file in Directory.GetFiles(samplesDir, "*.wav").OrderBy(p => p))
        {
            int? midiNote = ParseMidiNoteFromFileName(Path.GetFileNameWithoutExtension(file));
            if (midiNote is null)
            {
                continue;
            }

            using var reader = new AudioFileReader(file);
            ISampleProvider sampleProvider = reader.ToSampleProvider();
            int channels = sampleProvider.WaveFormat.Channels;

            var floatBuffer = new List<float>();
            var readBuf = new float[4096];
            int read;

            while ((read = sampleProvider.Read(readBuf.AsSpan())) > 0)
            {
                if (channels == 1)
                {
                    for (int i = 0; i < read; i++)
                    {
                        floatBuffer.Add(readBuf[i]);
                    }
                }
                else
                {
                    for (int i = 0; i < read; i += channels)
                    {
                        float sum = 0f;
                        for (int c = 0; c < channels && i + c < read; c++)
                        {
                            sum += readBuf[i + c];
                        }
                        floatBuffer.Add(sum / channels);
                    }
                }
            }

            if (floatBuffer.Count > 0)
            {
                _zones.Add(new SampleZone
                {
                    RootMidiNote = midiNote.Value,
                    Samples = floatBuffer.ToArray(),
                    SampleRate = sampleProvider.WaveFormat.SampleRate
                });
            }
        }
    }

    private static int? ParseMidiNoteFromFileName(string name)
    {
        string digits = new string(name.TakeWhile(char.IsDigit).ToArray());
        if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int note) &&
            note is >= 0 and <= 127)
        {
            return note;
        }

        return NoteNameToMidi(name);
    }

    private static int? NoteNameToMidi(string label)
    {
        string trimmed = label.Trim();
        if (trimmed.Length < 2)
        {
            return null;
        }

        int semitone = char.ToUpperInvariant(trimmed[0]) switch
        {
            'C' => 0,
            'D' => 2,
            'E' => 4,
            'F' => 5,
            'G' => 7,
            'A' => 9,
            'B' => 11,
            _ => -1
        };

        if (semitone < 0)
        {
            return null;
        }

        int idx = 1;
        if (idx < trimmed.Length && (trimmed[idx] == '#' || trimmed[idx] == 's'))
        {
            semitone = (semitone + 1) % 12;
            idx++;
        }
        else if (idx < trimmed.Length && (trimmed[idx] == 'b' || trimmed[idx] == 'f'))
        {
            semitone = (semitone + 11) % 12;
            idx++;
        }

        if (!int.TryParse(trimmed[idx..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int octave))
        {
            return null;
        }

        int midi = (octave + 1) * 12 + semitone;
        return midi is >= 0 and <= 127 ? midi : null;
    }

    private SampleZone? FindNearestZone(int targetMidiNote)
    {
        SampleZone? best = null;
        int bestDistance = int.MaxValue;

        foreach (var zone in _zones)
        {
            int dist = Math.Abs(zone.RootMidiNote - targetMidiNote);
            if (dist < bestDistance)
            {
                bestDistance = dist;
                best = zone;
            }
        }

        return best;
    }

    private int AllocateVoice()
    {
        for (int i = 0; i < StringCount; i++)
        {
            int idx = _voiceOrder[i];
            if (!_voices[idx].IsActive)
            {
                PromoteVoice(idx);
                return idx;
            }
        }

        int stolen = _voiceOrder[0];
        PromoteVoice(stolen);
        return stolen;
    }

    private void PromoteVoice(int voiceIndex)
    {
        int pos = Array.IndexOf(_voiceOrder, voiceIndex);
        if (pos < 0)
        {
            return;
        }

        for (int i = pos; i < StringCount - 1; i++)
        {
            _voiceOrder[i] = _voiceOrder[i + 1];
        }
        _voiceOrder[StringCount - 1] = voiceIndex;
    }

    public void TriggerNoteOn(byte note, byte velocity)
    {
        if (velocity == 0)
        {
            TriggerNoteOff(note);
            return;
        }

        SampleZone? zone = FindNearestZone(note);
        if (zone is null || zone.Samples.Length < 2)
        {
            return;
        }

        int vIdx = AllocateVoice();
        StringVoice voice = _voices[vIdx];

        double hostRate = Host.SampleRate > 0 ? Host.SampleRate : 44100.0;
        double semitoneDiff = note - zone.RootMidiNote;
        double pitchRatio = Math.Pow(2.0, semitoneDiff / 12.0);

        voice.Zone = zone;
        voice.MidiNote = note;
        voice.Playhead = 0.0;
        voice.StepPerSample = pitchRatio * (zone.SampleRate / hostRate);
        voice.Gain = velocity / 127f;
        voice.Envelope = 1.0f;
        voice.IsReleasing = false;
        voice.IsActive = true;
    }

    public void TriggerNoteOff(byte note)
    {
        for (int i = 0; i < StringCount; i++)
        {
            StringVoice voice = _voices[i];
            if (voice.IsActive && voice.MidiNote == note)
            {
                voice.IsReleasing = true;
            }
        }
    }

    public override void Process()
    {
        base.Process();
        Host.ProcessAllEvents();

        if (_stereoOutput is null)
        {
            return;
        }

        Span<float> left = _stereoOutput.GetAudioBuffer(0);
        Span<float> right = _stereoOutput.GetAudioBuffer(1);
        int numSamples = left.Length;

        double gainDb = GetParameter("master_gain").ProcessValue;
        float masterGain = (float)AudioPluginParameter.DBToLinear(gainDb);
        float dampingStep = (float)GetParameter("release_rate").ProcessValue;

        for (int s = 0; s < numSamples; s++)
        {
            float mix = 0f;

            for (int v = 0; v < StringCount; v++)
            {
                StringVoice voice = _voices[v];
                if (!voice.IsActive || voice.Zone is null)
                {
                    continue;
                }

                float[] data = voice.Zone.Samples;
                int maxIdx = data.Length - 2;
                int idx = (int)voice.Playhead;

                if (idx >= maxIdx)
                {
                    voice.IsActive = false;
                    continue;
                }

                float frac = (float)(voice.Playhead - idx);
                float sample = data[idx] + frac * (data[idx + 1] - data[idx]);

                if (voice.IsReleasing)
                {
                    voice.Envelope -= dampingStep;
                    if (voice.Envelope <= 0f)
                    {
                        voice.Envelope = 0f;
                        voice.IsActive = false;
                        continue;
                    }
                }

                mix += sample * voice.Gain * voice.Envelope;
                voice.Playhead += voice.StepPerSample;
            }

            float outSample = mix * masterGain;
            left[s] = outSample;
            right[s] = outSample;
        }
    }
}