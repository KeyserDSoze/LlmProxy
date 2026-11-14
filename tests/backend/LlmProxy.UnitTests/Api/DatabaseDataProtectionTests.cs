using System.Xml.Linq;
using LlmProxy.Api.Security;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmProxy.UnitTests.Api;

public sealed class DatabaseDataProtectionTests
{
    private sealed class Development : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static ServiceProvider Services(string database, string pepper)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Authentication:ApiKeyPepper"] = pepper }).Build();
        return new ServiceCollection()
            .AddLogging()
            .AddDbContext<GatewayDbContext>(options => options.UseInMemoryDatabase(database))
            .AddSingleton(new SensitiveDataProtector(configuration, new Development()))
            .BuildServiceProvider();
    }

    private static DatabaseDataProtectionRepository Repository(ServiceProvider services) => new(
        services.GetRequiredService<IServiceScopeFactory>(), services.GetRequiredService<SensitiveDataProtector>(), NullLogger<DatabaseDataProtectionRepository>.Instance);

    private static IDataProtector Protector(DatabaseDataProtectionRepository repository)
    {
        var services = new ServiceCollection().AddDataProtection().SetApplicationName("LlmProxy").Services
            .Configure<KeyManagementOptions>(options => options.XmlRepository = repository)
            .BuildServiceProvider();
        return services.GetRequiredService<IDataProtectionProvider>().CreateProtector("auth-cookie");
    }

    [Fact]
    public void A_cookie_protected_before_a_restart_is_still_readable_after_it()
    {
        var database = Guid.NewGuid().ToString("N");
        using var first = Services(database, "pepper-one");
        var cookie = Protector(Repository(first)).Protect("signed-in-admin");

        // A new process: new container, new service provider, same database.
        using var second = Services(database, "pepper-one");
        Assert.Equal("signed-in-admin", Protector(Repository(second)).Unprotect(cookie));
    }

    [Fact]
    public void Keys_are_stored_encrypted_and_never_as_readable_xml()
    {
        using var services = Services(Guid.NewGuid().ToString("N"), "pepper-one");
        Protector(Repository(services)).Protect("x");

        var rows = services.GetRequiredService<GatewayDbContext>().DataProtectionKeys.AsNoTracking().ToList();

        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.StartsWith("v1.", row.XmlCiphertext);
            Assert.DoesNotContain("<key", row.XmlCiphertext);
            Assert.DoesNotContain("masterKey", row.XmlCiphertext);
        });
    }

    [Fact]
    public void A_key_that_cannot_be_decrypted_is_skipped_and_a_new_one_is_used()
    {
        var database = Guid.NewGuid().ToString("N");
        using var first = Services(database, "pepper-one");
        var cookie = Protector(Repository(first)).Protect("signed-in-admin");

        using var second = Services(database, "a-different-pepper");
        var repository = Repository(second);
        Assert.Empty(repository.GetAllElements());
        // The old session cannot be read any more, but the gateway keeps working and issues cookies with a new key.
        var protector = Protector(repository);
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => protector.Unprotect(cookie));
        Assert.Equal("again", protector.Unprotect(protector.Protect("again")));
    }

    [Fact]
    public void Storing_a_key_twice_updates_it()
    {
        using var services = Services(Guid.NewGuid().ToString("N"), "pepper-one");
        var repository = Repository(services);

        repository.StoreElement(new XElement("key", "a"), "key-1");
        repository.StoreElement(new XElement("key", "b"), "key-1");

        var elements = repository.GetAllElements();
        Assert.Single(elements);
        Assert.Equal("b", elements.Single().Value);
    }
}
