namespace NSLabs.EFCore.Extensions.Samples.Models;

public class EnergyReading
{
    public int Id { get; set; }

    public string MeterId { get; set; } = string.Empty;

    public DateTime Date { get; set; }

    public double ConsumptionKwh { get; set; }

    public DateTime RecordedAt { get; set; }
}
