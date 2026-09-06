namespace NSLabs.EFCore.Extensions;

public sealed class BulkExecuteOptions
{
    public int MaxParametersPerCommand { get; set; } = 2000;

    public bool ThrowIfZeroAffected { get; set; }

    public int? CommandTimeout { get; set; }

    public BulkExecuteOptions Clone()
    {
        var clone = new BulkExecuteOptions();
        CopyTo(clone);
        return clone;
    }

    public void CopyTo(BulkExecuteOptions target)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.MaxParametersPerCommand = MaxParametersPerCommand;
        target.ThrowIfZeroAffected = ThrowIfZeroAffected;
        target.CommandTimeout = CommandTimeout;
    }

    internal void Validate()
    {
        if (MaxParametersPerCommand <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxParametersPerCommand),
                MaxParametersPerCommand,
                $"'{nameof(MaxParametersPerCommand)}' must be greater than zero.");
        }

        if (CommandTimeout is { } timeout && timeout < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CommandTimeout),
                timeout,
                $"'{nameof(CommandTimeout)}' must be greater than or equal to zero when set.");
        }
    }
}
