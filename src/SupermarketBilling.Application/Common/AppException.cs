namespace SupermarketBilling.Application.Common;

public enum ErrorKind
{
    Validation,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    Locked,
    TooManyRequests,
}

/// <summary>
/// An expected failure of a use case, translated by the API into an RFC 7807 problem response.
/// Messages are safe to show to users and never contain secrets.
/// </summary>
public sealed class AppException : Exception
{
    public AppException(ErrorKind kind, string code, string message)
        : base(message)
    {
        Kind = kind;
        Code = code;
    }

    public AppException(ErrorKind kind, string code, string message, Exception? innerException)
        : base(message, innerException)
    {
        Kind = kind;
        Code = code;
    }

    public AppException()
        : this(ErrorKind.Validation, "error", "The request could not be completed.")
    {
    }

    public AppException(string message)
        : this(ErrorKind.Validation, "error", message)
    {
    }

    public AppException(string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = ErrorKind.Validation;
        Code = "error";
    }

    public ErrorKind Kind { get; }

    public string Code { get; }

    public static AppException NotFound(string what) => new(ErrorKind.NotFound, "not_found", $"{what} was not found.");

    public static AppException Forbidden(string message = "You do not have permission to do this.") =>
        new(ErrorKind.Forbidden, "forbidden", message);

    public static AppException Validation(string code, string message) => new(ErrorKind.Validation, code, message);

    public static AppException Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);
}
