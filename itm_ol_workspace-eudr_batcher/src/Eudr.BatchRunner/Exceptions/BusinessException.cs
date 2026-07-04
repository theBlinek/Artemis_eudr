namespace Eudr.BatchRunner.Exceptions;

/// <summary>
/// Thrown when a business rule is violated during settlement (missing ROZLICZ_ID,
/// no RPR for ZWR, insufficient PKS qty, etc.).  The batch runner catches this,
/// rolls back the document transaction, records BUSINESS_EXCEPTION, and continues.
/// </summary>
public sealed class BusinessException(string message) : Exception(message);

/// <summary>
/// Thrown when settlement detects a compliance violation that must halt compensation
/// (e.g. reversing a RIN that has already been consumed by an RPR — §11 BR4 backstop).
/// </summary>
public sealed class ComplianceViolationException(string message) : Exception(message);
