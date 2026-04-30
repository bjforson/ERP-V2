// Type-forwarding shim for the NickFinance.Identity → NickERP.Platform.Identity
// extraction (Wave 3B / Track A.2).
//
// Every public type that NickFinance.Identity used to ship now lives in
// NickERP.Platform.Identity. The forwards below let any consumer that still
// has `using NickFinance.Identity;` keep compiling and resolving without a
// code change — the runtime sees the [TypeForwardedTo] attribute on this
// assembly and looks up the type in the platform assembly instead.
//
// Once every consumer has migrated to `using NickERP.Platform.Identity;`,
// this whole project (and the forwards below) can be deleted in a future
// round.

using System.Runtime.CompilerServices;

[assembly: TypeForwardedTo(typeof(NickERP.Platform.Identity.User))]
[assembly: TypeForwardedTo(typeof(NickERP.Platform.Identity.UserStatus))]
[assembly: TypeForwardedTo(typeof(NickERP.Platform.Identity.UserPhone))]
[assembly: TypeForwardedTo(typeof(NickERP.Platform.Identity.Role))]
[assembly: TypeForwardedTo(typeof(NickERP.Platform.Identity.UserRole))]
[assembly: TypeForwardedTo(typeof(NickERP.Platform.Identity.SecurityAuditEvent))]
[assembly: TypeForwardedTo(typeof(NickERP.Platform.Identity.SecurityAuditAction))]
[assembly: TypeForwardedTo(typeof(NickERP.Platform.Identity.SecurityAuditResult))]
[assembly: TypeForwardedTo(typeof(NickERP.Platform.Identity.RoleNames))]
[assembly: TypeForwardedTo(typeof(NickERP.Platform.Identity.IdentityDbContext))]
[assembly: TypeForwardedTo(typeof(NickERP.Platform.Identity.ITenantAccessor))]
[assembly: TypeForwardedTo(typeof(NickERP.Platform.Identity.NullTenantAccessor))]
[assembly: TypeForwardedTo(typeof(NickERP.Platform.Identity.FixedTenantAccessor))]
[assembly: TypeForwardedTo(typeof(NickERP.Platform.Identity.ISecurityAuditService))]
[assembly: TypeForwardedTo(typeof(NickERP.Platform.Identity.NoopSecurityAuditService))]
