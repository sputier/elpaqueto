namespace ElPaqueto.Security;

/// <summary>
/// The logging category of the security audit trail: an <c>ILogger&lt;Audit&gt;</c> logs under
/// <c>ElPaqueto.Security.Audit</c>, the category operators filter, route and retain.
/// </summary>
/// <remarks>
/// A marker type only, so that the audit logger can be injected like any other logger. The events are
/// declared in <see cref="Auditing.AuditLog"/> and <see cref="Users.UserAdministrationLog"/>; see
/// <c>docs/technical/audit-logging.md</c>.
/// </remarks>
internal sealed class Audit
{
    private Audit()
    {
    }
}
