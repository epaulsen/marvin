namespace Marvin.Api;

internal interface IVoiceTurnProcessor
{
    // The response, if present, must be 24 kHz mono PCM16 little-endian.
    Task<byte[]?> ProcessAsync(byte[] audio, CancellationToken cancellationToken);
}

internal sealed class NoOpVoiceTurnProcessor : IVoiceTurnProcessor
{
    public Task<byte[]?> ProcessAsync(byte[] audio, CancellationToken cancellationToken) =>
        Task.FromResult<byte[]?>(null);
}
