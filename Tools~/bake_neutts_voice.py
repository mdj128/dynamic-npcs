"""Bake a NeuTTS voice reference for the Dynamic NPCs Unity package.

One-time, dev-machine-only step (players never need this): encodes a voice
sample to NeuCodec codes and writes a JSON you import onto an NpcVoice asset
via its inspector ("Import NeuTTS Reference...").

    pip install neucodec librosa "torchao<0.18"

(torchao is pinned: neucodec asks for torchao>=0.12 with no upper bound, but
0.18 moved NF4Tensor out of torchao.dtypes, where the torchtune that neucodec
imports still looks for it.)

Usage:
    python bake_neutts_voice.py <sample.wav|.mp3> <transcript.txt|"text"> <out.json>
                                [encoder_checkpoint.bin]

No Hugging Face account is needed. NeuCodec's own from_pretrained hard-codes a
now-gated repo id, so this script loads the encoder weights from a local file
instead, downloading them from the package's GitHub release on first use. Pass a
path as the 4th argument to use a checkpoint you already have, or set
DYNAMICNPCS_NEUCODEC_CKPT to override the cache location.

Sample guidelines (from neuphonic/neutts): 3-15 seconds, mono, clean,
natural continuous speech, minimal silence.
"""
import hashlib
import json
import os
import sys
import urllib.request

# Neuphonic's NeuCodec encoder (Apache-2.0), mirrored so baking needs no account.
CKPT_URL = "https://github.com/mdj128/dynamic-npcs/releases/download/codec-v1/neucodec-encoder.bin"
CKPT_SHA256 = "30c3ea13ceeb2de693c56e5e33a1b7e00d44c95dcdd08a4ed0d552d0bf59ebdf"
CKPT_BYTES = 1160509432

# Upstream drops these when loading: the checkpoint carries decoder-side tensors
# this model does not define.
IGNORE_KEYS = ["fc_post_s", "SemanticDecoder"]


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def ensure_checkpoint(path):
    """Return a verified encoder checkpoint, downloading it if necessary."""
    if os.path.isfile(path) and os.path.getsize(path) == CKPT_BYTES:
        return path

    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    print(f"Downloading NeuCodec encoder ({CKPT_BYTES / 1e9:.2f} GB, one time)...")
    tmp = path + ".part"

    def progress(count, block, total):
        if total > 0:
            print(f"\r  {min(100.0, count * block * 100.0 / total):5.1f}%", end="", flush=True)

    urllib.request.urlretrieve(CKPT_URL, tmp, reporthook=progress)
    print()

    actual = sha256(tmp)
    if actual != CKPT_SHA256:
        os.remove(tmp)
        raise SystemExit(
            f"Encoder checkpoint failed verification.\n"
            f"  expected {CKPT_SHA256}\n  actual   {actual}"
        )
    os.replace(tmp, path)
    return path


def load_encoder(ckpt_path):
    """
    Build NeuCodec and load the weights directly.

    NeuCodec._from_pretrained asserts the model id is one of neuphonic's own
    repos and pulls it through huggingface_hub, which now needs an account
    because those repos are gated. The loading itself is just a filtered
    load_state_dict, replicated here against a local file. The one model still
    fetched from the Hub, facebook/w2v-bert-2.0, is ungated.
    """
    import torch
    from neucodec import NeuCodec

    model = NeuCodec(24_000, 480)
    state = torch.load(ckpt_path, map_location="cpu")
    state = {k: v for k, v in state.items() if not any(i in k for i in IGNORE_KEYS)}
    model.load_state_dict(state, strict=False)
    model.eval()
    return model


def main():
    if len(sys.argv) not in (4, 5):
        print(__doc__)
        sys.exit(1)

    wav_path, transcript_arg, out_path = sys.argv[1:4]
    ckpt_path = (
        sys.argv[4] if len(sys.argv) == 5
        else os.environ.get("DYNAMICNPCS_NEUCODEC_CKPT")
        or os.path.join(os.path.expanduser("~"), ".cache", "dynamicnpcs", "neucodec-encoder.bin")
    )

    import librosa
    import torch

    if os.path.isfile(transcript_arg):
        with open(transcript_arg, encoding="utf-8") as f:
            transcript = f.read().strip()
    else:
        transcript = transcript_arg.strip()

    ckpt_path = ensure_checkpoint(ckpt_path)
    print("Loading NeuCodec encoder...")
    codec = load_encoder(ckpt_path)

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
