"""Bake a NeuTTS voice reference for the Dynamic NPCs Unity package.

One-time, dev-machine-only step (players never need this): encodes a voice
sample WAV to NeuCodec codes and writes a JSON you import onto an NpcVoice
asset via its inspector ("Import NeuTTS Reference...").

Requires the NeuCodec *encoder*, which currently only exists in PyTorch:

    pip install neucodec librosa "torchao<0.18"

(torchao is pinned: neucodec asks for torchao>=0.12 with no upper bound, but
0.18 moved NF4Tensor out of torchao.dtypes, where the torchtune that neucodec
imports still looks for it.)

Usage:
    python bake_neutts_voice.py <sample.wav> <transcript.txt|"transcript text"> <out.json>

Sample guidelines (from neuphonic/neutts): 3-15 seconds, mono, clean,
natural continuous speech, minimal silence.
"""
import json
import os
import sys


def main():
    if len(sys.argv) != 4:
        print(__doc__)
        sys.exit(1)

    wav_path, transcript_arg, out_path = sys.argv[1:4]

    import librosa
    import torch
    from neucodec import NeuCodec

    if os.path.isfile(transcript_arg):
        with open(transcript_arg, encoding="utf-8") as f:
            transcript = f.read().strip()
    else:
        transcript = transcript_arg.strip()

    print("Loading NeuCodec encoder (first run downloads weights)...")
    codec = NeuCodec.from_pretrained("neuphonic/neucodec")
    codec.eval()

    wav, _ = librosa.load(wav_path, sr=16000, mono=True)
    duration = len(wav) / 16000.0
    if not 1.0 <= duration <= 30.0:
        print(f"WARNING: sample is {duration:.1f}s; 3-15s of clean speech works best.")

    wav_tensor = torch.from_numpy(wav).float().unsqueeze(0).unsqueeze(0)  # [1, 1, T]
    with torch.no_grad():
        codes = codec.encode_code(audio_or_path=wav_tensor).squeeze(0).squeeze(0)

    data = {
        "name": os.path.splitext(os.path.basename(wav_path))[0],
        "transcript": transcript,
        "codes": [int(c) for c in codes],
    }
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(data, f)

    print(f"Wrote {out_path}: {len(data['codes'])} codes ({len(data['codes']) / 50.0:.1f}s of reference)")


if __name__ == "__main__":
    main()
