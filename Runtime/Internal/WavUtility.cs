using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace DynamicNpcs
{
    /// <summary>
    /// Minimal WAV codec: AudioClip -> PCM16 WAV bytes (for uploading voice samples)
    /// and WAV bytes -> AudioClip (for playing synthesized speech returned by the server).
    /// Supports PCM 16/24/32-bit and IEEE float32, including WAVE_FORMAT_EXTENSIBLE.
    /// </summary>
    public static class WavUtility
    {
        public static byte[] FromAudioClip(AudioClip clip)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));

            var samples = new float[clip.samples * clip.channels];
            if (!clip.GetData(samples, 0))
                throw new InvalidOperationException(
                    $"Could not read sample data from AudioClip '{clip.name}'. " +
                    "Set its import Load Type to 'Decompress On Load'.");

            int dataLength = samples.Length * 2;
            using (var ms = new MemoryStream(44 + dataLength))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + dataLength);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);
                w.Write((short)1); // PCM
                w.Write((short)clip.channels);
                w.Write(clip.frequency);
                w.Write(clip.frequency * clip.channels * 2); // byte rate
                w.Write((short)(clip.channels * 2)); // block align
                w.Write((short)16); // bits per sample
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(dataLength);
                foreach (float f in samples)
                {
                    int v = Mathf.RoundToInt(Mathf.Clamp(f, -1f, 1f) * 32767f);
                    w.Write((short)v);
                }
                return ms.ToArray();
            }
        }

        public static AudioClip ToAudioClip(byte[] wav, string clipName = "wav")
        {
            if (wav == null || wav.Length < 44)
                throw new ArgumentException("WAV data is null or too short.");
            if (wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F' ||
                wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E')
                throw new ArgumentException("Not a RIFF/WAVE file.");

            int channels = 0, sampleRate = 0, bitsPerSample = 0;
            ushort audioFormat = 0;
            int dataOffset = -1, dataSize = 0;

            int pos = 12;
            while (pos + 8 <= wav.Length)
            {
                string chunkId = Encoding.ASCII.GetString(wav, pos, 4);
                int chunkSize = BitConverter.ToInt32(wav, pos + 4);
                int body = pos + 8;

                if (chunkId == "fmt " && body + 16 <= wav.Length)
                {
                    audioFormat = BitConverter.ToUInt16(wav, body);
                    channels = BitConverter.ToUInt16(wav, body + 2);
                    sampleRate = BitConverter.ToInt32(wav, body + 4);
                    bitsPerSample = BitConverter.ToUInt16(wav, body + 14);

                    // WAVE_FORMAT_EXTENSIBLE: real format lives in the SubFormat GUID.
                    if (audioFormat == 0xFFFE && body + 26 <= wav.Length)
                        audioFormat = BitConverter.ToUInt16(wav, body + 24);
                }
                else if (chunkId == "data")
                {
                    dataOffset = body;
                    dataSize = Mathf.Min(chunkSize, wav.Length - body);
                }

                pos = body + chunkSize;
                if ((chunkSize & 1) == 1) pos++; // chunks are word-aligned
            }

            if (dataOffset < 0) throw new ArgumentException("WAV file has no 'data' chunk.");
            if (channels <= 0 || sampleRate <= 0) throw new ArgumentException("WAV file has an invalid 'fmt ' chunk.");

            float[] samples;
            switch (audioFormat)
            {
                case 1 when bitsPerSample == 16:
                {
                    int count = dataSize / 2;
                    samples = new float[count];
                    for (int i = 0; i < count; i++)
                        samples[i] = BitConverter.ToInt16(wav, dataOffset + i * 2) / 32768f;
                    break;
                }
                case 1 when bitsPerSample == 24:
                {
                    int count = dataSize / 3;
                    samples = new float[count];
                    for (int i = 0; i < count; i++)
                    {
                        int o = dataOffset + i * 3;
                        int v = (wav[o] << 8 | wav[o + 1] << 16 | wav[o + 2] << 24) >> 8;
                        samples[i] = v / 8388608f;
                    }
                    break;
                }
                case 1 when bitsPerSample == 32:
                {
                    int count = dataSize / 4;
                    samples = new float[count];
                    for (int i = 0; i < count; i++)
                        samples[i] = BitConverter.ToInt32(wav, dataOffset + i * 4) / 2147483648f;
                    break;
                }
                case 3 when bitsPerSample == 32:
                {
                    int count = dataSize / 4;
                    samples = new float[count];
                    for (int i = 0; i < count; i++)
                        samples[i] = BitConverter.ToSingle(wav, dataOffset + i * 4);
                    break;
                }
                default:
                    throw new NotSupportedException($"Unsupported WAV format {audioFormat} at {bitsPerSample}-bit.");
            }

            var clip = AudioClip.Create(clipName, samples.Length / channels, channels, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
