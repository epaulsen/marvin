using Microsoft.ML.OnnxRuntime;

namespace Marvin.Api.Voice;

internal interface IVoiceActivityDetector
{
    float Evaluate(ReadOnlySpan<byte> pcm);
    void Reset();
}

internal interface IVoiceActivityDetectorFactory
{
    IVoiceActivityDetector Create();
}

internal sealed class SileroVadModel : IVoiceActivityDetectorFactory, IDisposable
{
    private readonly InferenceSession session;

    public SileroVadModel(IWebHostEnvironment environment)
    {
        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 1 };
        session = new InferenceSession(Path.Combine(environment.ContentRootPath, "Models", "silero_vad.onnx"), options);
    }

    public IVoiceActivityDetector Create() => new SileroVadDetector(session);

    public void Dispose() => session.Dispose();
}
