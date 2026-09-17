using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace VpnUs.Core.Ipc;

/// <summary>
/// ACL именованного канала. По умолчанию NamedPipeServerStream получает DACL процесса-создателя:
/// для службы LocalSystem обычный пользователь не может подключиться к каналу (Access denied),
/// поэтому права задаются явно. Авторизация запросов — токен + список SID (ipc.allow).
/// </summary>
public static class IpcPipeFactory
{
    public static PipeSecurity Create()
    {
        var security = new PipeSecurity();

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));

        return security;
    }

    public static NamedPipeServerStream CreateServer(
        string pipeName,
        int maxInstances = NamedPipeServerStream.MaxAllowedServerInstances)
        => NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            Create());
}
