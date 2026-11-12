namespace HomelabInventory.Web.Models;

/// <summary>
/// An application hosted on the LAN. <see cref="NormalizedName"/> is derived
/// from <see cref="Name"/> by <see cref="Services.TrackedAppService"/> and is
/// what the case-insensitive uniqueness constraint indexes on.
/// </summary>
public class TrackedApp
{
    public int Id { get; set; }

    // Identity
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string? Description { get; set; }

    // Location
    public string HostnameOrIp { get; set; } = "";
    public int? Port { get; set; }
    public string? Url { get; set; }

    // Deployment
    public string? DeployedOn { get; set; }
    public ServedVia ServedVia { get; set; } = ServedVia.Container;
    public string? ProxyPath { get; set; }

    // Source
    public string? RepoUrl { get; set; }
    public string? Branch { get; set; } = "main";
    public string? ContainerImage { get; set; }
}
