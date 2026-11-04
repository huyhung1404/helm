namespace Helm.Connector.Hosting;

/// <summary>
/// One thing <c>helm-connector serve</c> runs beside sync until it stops: the hold reports, the MCP socket for Fairy
/// Core. A later part (the phone's agent jobs, forwarded to Fairy Core) is one more of these, registered as
/// <c>IServePart</c>; nothing else in serve changes for it.
/// </summary>
internal interface IServePart : IDisposable
{
    void Start();
}
