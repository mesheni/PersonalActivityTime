namespace Pat.Infrastructure;

public enum OperationStatus { Success, Cancelled, NoChanges, Error }

public sealed record OperationResult(OperationStatus Status, string Message, string? Path = null)
{
    public bool IsSuccess => Status == OperationStatus.Success;
    public static OperationResult Failure(Exception error) => new(OperationStatus.Error, error.Message);
}
