namespace Drps.Shared.Exceptions;

// Thrown by LedgerPositionWriter before any Position row is touched, when a caller supplies a
// non-positive value for a field that must represent a real, tradable price or share quantity
// (EntryPrice/EntryQuantity on open, ExitPrice/ExitQuantity on close). Same "flag before
// persisting anything, not after" pattern as ConfigurationMissingException - refusing the call
// up front means a bad caller value never reaches storage in a half-valid state.
public class InvalidPositionValueException : Exception
{
    public string FieldName { get; }
    public decimal Value { get; }

    public InvalidPositionValueException(string fieldName, decimal value)
        : base($"{fieldName} must be positive, got {value}.")
    {
        FieldName = fieldName;
        Value = value;
    }
}
