using VpnUs.Core.Storage;
using Xunit;

namespace VpnUs.Core.Tests;

/// <summary>
/// Регрессия: служба должна регистрироваться по тому же пути, откуда запущен установщик,
/// если он уже в Program Files или в портативной папке (иначе SCM стартует устаревший
/// бинарник и падает с ошибкой 1053).
/// </summary>
public class ServiceInstallPathResolverTests
{
    private const string ProgramFiles = @"C:\Program Files";
    private const string Target = @"C:\Program Files\VpnUs";

    [Fact]
    public void Installed_service_registers_its_own_path_without_copying()
    {
        const string process = @"C:\Program Files\VpnUs\service\VpnUs.Service.exe";

        var (deploy, exe) = ServiceInstallPathResolver.Resolve(process, Target, ProgramFiles, isPortable: false);

        Assert.False(deploy);
        Assert.Equal(process, exe);
    }

    [Fact]
    public void Portable_service_registers_its_own_path_without_copying()
    {
        const string process = @"D:\VpnUs-portable\service\VpnUs.Service.exe";

        var (deploy, exe) = ServiceInstallPathResolver.Resolve(process, Target, ProgramFiles, isPortable: true);

        Assert.False(deploy);
        Assert.Equal(process, exe);
    }

    [Fact]
    public void Build_output_service_is_deployed_to_program_files()
    {
        const string process = @"C:\projects\vpnus_desktop\src\VpnUs.Service\bin\Release\net8.0-windows\VpnUs.Service.exe";

        var (deploy, exe) = ServiceInstallPathResolver.Resolve(process, Target, ProgramFiles, isPortable: false);

        Assert.True(deploy);
        Assert.Equal(@"C:\Program Files\VpnUs\VpnUs.Service.exe", exe);
    }

    [Fact]
    public void Program_files_check_is_case_insensitive()
    {
        const string process = @"c:\PROGRAM FILES\VpnUs\service\VpnUs.Service.exe";

        var (deploy, _) = ServiceInstallPathResolver.Resolve(process, Target, ProgramFiles, isPortable: false);

        Assert.False(deploy);
    }

    [Fact]
    public void Source_equal_to_target_is_not_copying_to_itself()
    {
        const string process = @"C:\Program Files\VpnUs\VpnUs.Service.exe";

        var (deploy, exe) = ServiceInstallPathResolver.Resolve(process, @"C:\Program Files\VpnUs\", ProgramFiles, isPortable: false);

        Assert.False(deploy);
        Assert.Equal(process, exe);
    }

    [Fact]
    public void Folder_with_similar_prefix_is_not_treated_as_program_files()
    {
        // C:\Program Files (x86) и C:\ProgramFilesExtra не должны считаться "внутри Program Files"
        const string process = @"C:\ProgramFilesExtra\VpnUs.Service.exe";

        var (deploy, _) = ServiceInstallPathResolver.Resolve(process, Target, ProgramFiles, isPortable: false);

        Assert.True(deploy);
    }
}
