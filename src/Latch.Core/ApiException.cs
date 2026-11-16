using Latch.Core.Contract;

namespace Latch.Core;

/// <summary>An expected failure with a code; the API wrapper turns it into an <see cref="ApiError"/>.</summary>
public sealed class ApiException(ErrorCode code) : Exception(code.ToString())
{
    public ErrorCode Code { get; } = code;
}
