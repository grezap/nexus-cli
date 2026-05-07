namespace Nexus.Cli.Core;

/// <summary>
/// Trim/AOT-safe Result type. No exceptions for control flow; adapters return
/// <see cref="Result.Fail{T}(string)"/> on transport / auth / parse errors and
/// the CLI surfaces them as red banners.
/// </summary>
public readonly record struct Result<T>(T? Value, string? Error)
{
    public bool IsOk => Error is null;
    public bool IsFail => Error is not null;
}

public static class Result
{
    public static Result<T> Ok<T>(T value) => new(value, null);
    public static Result<T> Fail<T>(string error) => new(default, error);
}
