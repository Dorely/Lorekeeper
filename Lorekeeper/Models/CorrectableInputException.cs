namespace Lorekeeper.Models;

/// <summary>
/// Rejects a value at the point it enters the app. The message names the field and states the fix,
/// such as the exact valid options, contains no paths or secrets, and reaches every caller intact.
/// </summary>
public sealed class CorrectableInputException(string message) : ArgumentException(message);
