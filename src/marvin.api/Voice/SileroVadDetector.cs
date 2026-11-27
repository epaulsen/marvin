using System.Buffers.Binary;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Marvin.Api.Voice;

internal sealed class SileroVadDetector(InferenceSession session) : IVoiceActivityDetector
{
    private float[] state = new float[256];
    private readonly float[] context = new float[64];

    public float Evaluate(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length != 1024)
            throw new ArgumentException("Expected 512 PCM16 samples.", nameof(pcm));

        var samples = new float[576];
        context.CopyTo(samples, 0);
        for (var i = 0; i < 512; i++)
            samples[64 + i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2)) / 32768f;

        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(samples, [1, 576])),
            NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(state, [2, 1, 128])),
            NamedOnnxValue.CreateFromTensor("sr", new DenseTensor<long>(new long[] { 16000 }, [1]))
        };
        using var outputs = session.Run(inputs);
        var probability = outputs.First(x => x.Name == "output").AsTensor<float>().First();
        state = outputs.First(x => x.Name == "stateN").AsTensor<float>().ToArray();
        Array.Copy(samples, 512, context, 0, context.Length);
        return probability;
    }

    public void Reset()
    {
        Array.Clear(state);
        Array.Clear(context);
    }
}
