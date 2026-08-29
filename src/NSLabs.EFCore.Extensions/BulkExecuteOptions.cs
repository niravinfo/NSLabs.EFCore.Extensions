namespace NSLabs.EFCore.Extensions;

public sealed class BulkExecuteOptions
{
    public int MaxParametersPerCommand { get; set; } = 2000;

    public bool ThrowIfZeroAffected { get; set; }

    public int? CommandTimeout { get; set; }

    public Action<string>? OnCommandText { get; set; }
}
