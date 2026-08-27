using System;
using UnityEngine;
// Sentis was renamed to Inference Engine (com.unity.ai.inference) in Unity 6.1;
// the API is the same, only the package id and namespace changed.
#if DYNAMICNPCS_INFERENCE
using Inference = Unity.InferenceEngine;
#elif DYNAMICNPCS_SENTIS
using Inference = Unity.Sentis;
#endif

namespace DynamicNpcs
{
    /// <summary>
    /// Decodes NeuCodec speech codes (50 Hz, single codebook) to 24 kHz mono PCM.
    /// </summary>
    public interface INeuCodecDecoder : IDisposable
    {
        int SampleRate { get; }
        float[] Decode(int[] codes);
    }

#if DYNAMICNPCS_SENTIS || DYNAMICNPCS_INFERENCE
    /// <summary>
    /// Runs the neuphonic/neucodec-onnx-decoder model via Unity Inference Engine /
    /// Sentis (in-engine ONNX inference, no external process). Input: codes int32
    /// [1,1,T]. Output: waveform float [1,1,T*480] at 24 kHz.
    /// </summary>
    public class SentisNeuCodecDecoder : INeuCodecDecoder
    {
        private readonly Inference.Worker _worker;
        private readonly string _inputName;

        public int SampleRate => 24000;

        public SentisNeuCodecDecoder(UnityEngine.Object modelAsset)
        {
            var asset = modelAsset as Inference.ModelAsset;
            if (asset == null)
                throw new TtsException(
                    "Neu Codec Decoder in settings must be an Inference Engine / Sentis " +
                    "ModelAsset (import neucodec model.onnx into the project and assign it).");

            var model = Inference.ModelLoader.Load(asset);
            _inputName = model.inputs[0].name;
            _worker = new Inference.Worker(model, Inference.BackendType.CPU);
        }

        public float[] Decode(int[] codes)
        {
            if (codes == null || codes.Length == 0)
                return Array.Empty<float>();

            using (var input = new Inference.Tensor<int>(
                       new Inference.TensorShape(1, 1, codes.Length), codes))
            {
                _worker.SetInput(_inputName, input);
                _worker.Schedule();
                // PeekOutput returns a tensor owned by the worker - clone it, don't dispose it.
                var output = _worker.PeekOutput() as Inference.Tensor<float>;
                using (var cpu = output.ReadbackAndClone())
                {
                    return cpu.DownloadToArray();
                }
            }
        }

        public void Dispose() => _worker?.Dispose();
    }
#endif
}
