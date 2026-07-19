namespace VppSpy.GoodWe;

public sealed class GoodWeOptions
{
    public const string SectionName = "GoodWe";

    public string Host { get; set; } = "";
    public int Port { get; set; } = 8899;
    public byte CommAddress { get; set; } = 0xF7;
    public int TimeoutMs { get; set; } = 2000;
    public int DiscoveryPort { get; set; } = 48899;
    public int DiscoveryTimeoutMs { get; set; } = 3000;
}
