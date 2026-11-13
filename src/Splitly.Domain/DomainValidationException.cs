namespace Splitly.Domain;

/// <summary>A business-rule violation that should surface to the caller as a 400/422.</summary>
public sealed class DomainValidationException(string message) : Exception(message);
