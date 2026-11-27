using Microsoft.Extensions.Options;

namespace Marvin.Api.Configuration;

// Preserve configuration reloads while consumers depend on IOptions<T>.
internal sealed class ReloadingOptions<T>(IOptionsMonitor<T> monitor) : IOptions<T> where T : class
{
    public T Value => monitor.CurrentValue;
}
