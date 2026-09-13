using FlexFetch.Config;

namespace FlexFetch.Tests;

[TestClass]
public sealed class ConfigRegistryTests
{
    [TestMethod]
    public void Registry_DeclaresAllKeysWithDefaults()
    {
        // Keys whose empty default is intentional (e.g. proxy must not be
        // hardcoded; route rules are optional; the dedicated installer proxy
        // is usually unset so installers follow the primary proxy or direct).
        var allowEmpty = new HashSet<string>
        {
            ConfigKeys.Proxy,
            ConfigKeys.RouteRules,
            ConfigKeys.NetworkHttpProxy,
        };

        var keys = typeof(ConfigKeys).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.IsGreaterThan(10, keys.Count);
        foreach (var key in keys)
        {
            Assert.IsTrue(ConfigRegistry.ContainsKey(key), $"Missing config item for key: {key}");
            if (!allowEmpty.Contains(key))
            {
                Assert.IsFalse(string.IsNullOrEmpty(ConfigRegistry.GetDefault(key)), $"Empty default for key: {key}");
            }
        }
    }

    [TestMethod]
    public void Defaults_PassValidation()
    {
        foreach (var item in ConfigRegistry.All)
        {
            Assert.IsNull(item.Validator(item.DefaultValue), $"Invalid default for key: {item.Key}");
        }
    }

    [TestMethod]
    public void Validate_RejectsInvalidValues()
    {
        Assert.IsNotNull(ConfigRegistry.Validate(ConfigKeys.MaxConcurrency, "0"));
        Assert.IsNotNull(ConfigRegistry.Validate(ConfigKeys.MaxConcurrency, "-1"));
        Assert.IsNotNull(ConfigRegistry.Validate(ConfigKeys.MaxConcurrency, "abc"));
        Assert.IsNotNull(ConfigRegistry.Validate(ConfigKeys.RegistrationPolicy, "Unknown"));
        Assert.IsNotNull(ConfigRegistry.Validate(ConfigKeys.SessionHumanize, "maybe"));
        Assert.IsNotNull(ConfigRegistry.Validate(ConfigKeys.InactiveDays, "-5"));
        Assert.IsNotNull(ConfigRegistry.Validate(ConfigKeys.DataDir, " "));
    }

    [TestMethod]
    public void Validate_AcceptsValidValues()
    {
        Assert.IsNull(ConfigRegistry.Validate(ConfigKeys.MaxConcurrency, "4"));
        Assert.IsNull(ConfigRegistry.Validate(ConfigKeys.RegistrationPolicy, "approval"));
        Assert.IsNull(ConfigRegistry.Validate(ConfigKeys.InactiveDays, "0"));
        Assert.IsNull(ConfigRegistry.Validate(ConfigKeys.Proxy, string.Empty));
    }

    [TestMethod]
    public void Validate_RejectsUnknownKey()
    {
        Assert.IsNotNull(ConfigRegistry.Validate("unknown.key", "value"));
    }

    [TestMethod]
    public void Get_ThrowsForUnknownKey()
    {
        Assert.ThrowsExactly<KeyNotFoundException>(() => ConfigRegistry.Get("unknown.key"));
    }
}
