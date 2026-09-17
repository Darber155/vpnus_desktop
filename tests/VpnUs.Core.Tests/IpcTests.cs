using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using VpnUs.Core.Ipc;
using Xunit;

namespace VpnUs.Core.Tests;

public class IpcTests
{
    [Fact]
    public void PipeSecurity_AllowsAuthenticatedUsersReadWrite()
    {
        var security = IpcPipeFactory.Create();
        var authenticated = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        var rule = security
            .GetAccessRules(includeExplicit: true, includeInherited: false, targetType: typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Single(r => authenticated.Equals(r.IdentityReference));

        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.True(rule.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite));
    }

    [Fact]
    public void PipeSecurity_KeepsSystemAndAdministratorsFullControl()
    {
        var security = IpcPipeFactory.Create();

        foreach (var sid in new[]
                 {
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                 })
        {
            var rule = security
                .GetAccessRules(true, false, typeof(SecurityIdentifier))
                .Cast<PipeAccessRule>()
                .Single(r => sid.Equals(r.IdentityReference));

            Assert.Equal(PipeAccessRights.FullControl, rule.PipeAccessRights);
        }
    }

    [Fact]
    public void PipeSecurity_DisablesInheritance()
    {
        // Иначе DACL процесса (для LocalSystem он не даёт прав обычным пользователям) мог бы
        // перекрыть явные разрешения, и UI получал бы Access denied.
        Assert.True(IpcPipeFactory.Create().AreAccessRulesProtected);
    }

    [Fact]
    public async Task PipeServer_CreatedByFactory_AcceptsConnection()
    {
        var name = "vpnus-test-" + Guid.NewGuid().ToString("N");

        await using var server = IpcPipeFactory.CreateServer(name);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

        var connect = client.ConnectAsync(5000);
        await server.WaitForConnectionAsync();
        await connect;

        Assert.True(server.IsConnected);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public void IpcErrors_DescribeAccessDenied_WithActionableText()
    {
        var text = IpcErrors.Describe(new UnauthorizedAccessException("Access to the path 'C:\\ProgramData\\VpnUs' is denied."));

        Assert.Contains("install", text);
        Assert.Contains("ProgramData", text);
    }

    [Fact]
    public void IpcErrors_DescribeOtherExceptions()
    {
        Assert.Equal("boom", IpcErrors.Describe(new InvalidOperationException("boom")));
        Assert.Contains("ввода-вывода", IpcErrors.Describe(new IOException("disk")));
    }
}
