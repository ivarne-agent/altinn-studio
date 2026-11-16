namespace Altinn.App.Core.Internal.Data;

/// <summary>
/// Indicates that the app changed the instance in a way a save cannot send, such as a property Storage does not take
/// in an instance mutation, or a data value that the save would overwrite with one derived from form data.
/// </summary>
/// <remarks>
/// Running the same code again makes the same change, so a workflow callback reports this as a non-retryable failure.
/// </remarks>
internal sealed class InstanceChangeNotSavableException : InvalidOperationException
{
    public InstanceChangeNotSavableException(string message)
        : base(message) { }
}
