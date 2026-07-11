namespace Nexus.Cli.Core;

/// <summary>
/// Trim/AOT-safe Result type. No exceptions for control flow; adapters return
/// <see cref="Result.Fail{T}(string)"/> on transport / auth / parse errors and
/// the CLI surfaces them as red banners.
/// </summary>
/// <param name="Value">The success payload; <c>null</c> when the operation failed.</param>
/// <param name="Error">The failure message; <c>null</c> when the operation succeeded.</param>
public readonly record struct Result<T>(T? Value, string? Error)
{
    /// <summary>True when no error was recorded (the operation succeeded).</summary>
    public bool IsOk => Error is null;

    /// <summary>True when an error was recorded (the operation failed).</summary>
    public bool IsFail => Error is not null;
}

/// <summary>Factory helpers for constructing <see cref="Result{T}"/> values.</summary>
public static class Result
{
    /// <summary>Wraps a success payload in an ok <see cref="Result{T}"/>.</summary>
    public static Result<T> Ok<T>(T value) => new(value, null);

    /// <summary>Wraps a failure message in a failed <see cref="Result{T}"/>.</summary>
    public static Result<T> Fail<T>(string error) => new(default, error);
}
