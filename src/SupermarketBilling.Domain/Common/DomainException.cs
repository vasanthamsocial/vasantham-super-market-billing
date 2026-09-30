namespace SupermarketBilling.Domain.Common;

/// <summary>
/// A business rule was violated. <see cref="Code"/> is a stable machine-readable identifier
/// (for example <c>store.code_invalid</c>) that the API returns alongside the human message.
/// </summary>
public sealed class DomainException : Exception
{
    public DomainException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public DomainException()
        : this("domain.error", "A business rule was violated.")
    {
    }

    public DomainException(string message)
        : this("domain.error", message)
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "domain.error";
    }

    public string Code { get; }
}
