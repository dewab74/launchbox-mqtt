namespace LaunchBoxMqtt.Core.Payloads;

/// <summary>Payloads on the availability topic; also the MQTT Last Will.</summary>
public static class Availability
{
    public const string Online = "online";
    public const string Offline = "offline";
}
