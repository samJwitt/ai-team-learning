namespace HomelabInventory.Web.Models;

/// <summary>How a tracked app is served on the LAN.</summary>
public enum ServedVia
{
    Container,
    Systemd,
    Static,
    Other,
}
