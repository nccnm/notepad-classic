using NotepadClassic.Core.Editing;
using NotepadClassic.Core.Settings;
using NotepadClassic.Platform.Interop;
using NotepadClassic.Platform.Settings;

namespace NotepadClassic.Platform.Tests.Settings;

/// <summary>
/// The registry store on a temporary application hive (<c>RegLoadAppKey</c>): never the real
/// <c>HKCU\Software\NotepadClassicPort</c> or <c>HKCU\Software\Microsoft\Notepad</c>.
/// </summary>
public sealed unsafe class SettingsRegistryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("npport-hive-").FullName;
    private readonly Win32SettingsRegistry _registry;

    public SettingsRegistryTests()
    {
        _registry = Win32SettingsRegistry.ForHive(Path.Combine(_dir, "settings.dat"));
    }

    public void Dispose()
    {
        _registry.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // The hive is unloaded asynchronously after its last handle closes.
        }
    }

    private RegistrySettingsStore Store() => new(_registry, new SettingsDefaults(false, "&f", "Page &p"));

    /// <summary>Writes a raw value of any kind and size under one of the store's keys.</summary>
    private void SetRaw(SettingsLocation location, string name, int kind, byte[] data)
    {
        var path = _registry.PathOf(location);
        using var root = Root();
        Advapi32.RegCreateKey(root.Handle, path, out var key).Should().Be(0);
        fixed (byte* p = data)
        {
            Advapi32.RegSetValueEx(key, name, 0, kind, p, data.Length).Should().Be(0);
        }

        _ = Advapi32.RegCloseKey(key);
    }

    private static byte[] Sz(string s) => System.Text.Encoding.Unicode.GetBytes(s + "\0");

    /// <summary>The hive root, reached through a second <c>RegLoadAppKey</c> of the same file.</summary>
    private RootHandle Root()
    {
        Advapi32.RegLoadAppKey(Path.Combine(_dir, "settings.dat"), out var hive, Advapi32.KEY_ALL_ACCESS, 0, 0).Should().Be(0);
        return new RootHandle(hive);
    }

    private sealed class RootHandle(Microsoft.Win32.SafeHandles.SafeRegistryHandle hive) : IDisposable
    {
        public nint Handle => hive.DangerousGetHandle();

        public void Dispose() => hive.Dispose();
    }

    [Fact]
    [Trait("Doc", "parity.md#registry")]
    public void First_load_creates_the_key_with_five_zero_dwords()
    {
        _registry.Open(SettingsLocation.User).Should().BeNull();

        var settings = Store().Load();

        settings.Font.Should().Be(FontSpec.Default);
        using var key = _registry.Open(SettingsLocation.User);
        key.Should().NotBeNull();
        foreach (var name in SettingsSchema.CreatedAsZero)
        {
            key!.TryReadDword(name, out var value).Should().BeTrue();
            value.Should().Be(0);
        }

        key!.TryReadDword(SettingsSchema.Wrap, out _).Should().BeFalse();
    }

    [Fact]
    [Trait("Doc", "parity.md#registry")]
    public void Kinds_and_sizes_are_checked_like_RegQueryValueExW()
    {
        SetRaw(SettingsLocation.User, "dword", Advapi32.REG_DWORD, BitConverter.GetBytes(42));
        SetRaw(SettingsLocation.User, "qword", 11, BitConverter.GetBytes(42L));
        SetRaw(SettingsLocation.User, "bigbinary", 3, new byte[8]);
        SetRaw(SettingsLocation.User, "sz", Advapi32.REG_SZ, Sz("hello"));
        SetRaw(SettingsLocation.User, "expand", 2, Sz("hello"));
        SetRaw(SettingsLocation.User, "nonul", Advapi32.REG_SZ, System.Text.Encoding.Unicode.GetBytes("abcd"));

        using var key = _registry.Open(SettingsLocation.User)!;

        key.TryReadDword("dword", out var dword).Should().BeTrue();
        dword.Should().Be(42);
        key.TryReadDword("qword", out _).Should().BeFalse();
        key.TryReadDword("bigbinary", out _).Should().BeFalse();
        key.TryReadDword("sz", out _).Should().BeFalse();
        key.TryReadDword("missing", out _).Should().BeFalse();

        key.TryReadString("sz", 10, out var sz).Should().BeTrue();
        sz.Should().Be("hello\0");
        key.TryReadString("sz", 6, out _).Should().BeTrue("6 WCHARs hold hello and its NUL");
        key.TryReadString("sz", 5, out _).Should().BeFalse("ERROR_MORE_DATA");
        key.TryReadString("expand", 10, out _).Should().BeFalse();
        key.TryReadString("dword", 10, out _).Should().BeFalse();
        // Data that fills the buffer exactly without a NUL is refused (ERROR_MORE_DATA, observed
        // here): the API keeps room for a terminator, so the original's buffer is always terminated.
        key.TryReadString("nonul", 4, out _).Should().BeFalse();
        key.TryReadString("nonul", 5, out var nonul).Should().BeTrue();
        nonul.Should().StartWith("abcd");
    }

    [Fact]
    [Trait("Doc", "parity.md#registry")]
    public void Settings_round_trip_through_the_hive()
    {
        var store = Store();
        var settings = store.Load();
        settings.Font = new FontSpec("Consolas", 110, 700, Italic: true);
        settings.FontChanged = true;
        settings.WordWrap = true;
        settings.ViewChanged = true;
        settings.SearchString = "tìm";
        settings.Reverse = true;
        settings.SearchChanged = true;
        settings.Header = "&d";
        settings.PageSetupChanged = true;

        store.Save(settings, new WindowRect(25, 30, 825, 630));
        var loaded = Store().Load();

        loaded.Font.Should().Be(new FontSpec("Consolas", 110, 700, Italic: true));
        loaded.WordWrap.Should().BeTrue();
        loaded.SearchString.Should().Be("tìm");
        loaded.Reverse.Should().BeTrue();
        loaded.Header.Should().Be("&d");
        loaded.WindowPosition.Should().Be(new WindowPosition(25, 30, 800, 600));
    }

    [Fact]
    [Trait("Doc", "parity.md#registry")]
    public void Machine_default_font_and_import_read_their_own_keys()
    {
        SetRaw(SettingsLocation.MachineDefaultFonts, SettingsSchema.FaceName, Advapi32.REG_SZ, Sz("Consolas"));
        SetRaw(SettingsLocation.MachineDefaultFonts, SettingsSchema.PointSize, Advapi32.REG_DWORD, BitConverter.GetBytes(110));
        SetRaw(SettingsLocation.Legacy, SettingsSchema.StatusBar, Advapi32.REG_DWORD, BitConverter.GetBytes(0));
        SetRaw(SettingsLocation.Legacy, SettingsSchema.ReplaceString, Advapi32.REG_SZ, Sz("thay"));

        var settings = Store().Load();

        settings.Font.Should().Be(new FontSpec("Consolas", 110));
        settings.StatusBar.Should().BeFalse();
        settings.ReplaceString.Should().Be("thay");
        using var legacy = _registry.Open(SettingsLocation.Legacy)!;
        legacy.TryReadDword(SettingsSchema.Reverse, out _).Should().BeFalse("the original's key is never written");
    }
}
