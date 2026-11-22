using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using PluginHost;

namespace StackedDeckTracker.Services;

/// <summary>
/// Abstracts PluginHost services so the plugin runs identically whether installed
/// as an API 1 package (*.plugin.zip / folder) or as a standalone loose *.plugin.dll.
/// </summary>
public interface IPluginHostBridge : IDisposable
{
    string DataDirectory { get; }
    Action Show { get; }
    void Log(string message);
    void Notify(string message);
    JsonObject ReadSettings();
    void SaveSettings(JsonObject settings);
    bool TryRegisterHotkey(Keys chord, Action action);
}

internal sealed class PackagedHostBridge(PluginServices services) : IPluginHostBridge
{
    public string DataDirectory => services.DataDirectory;
    public Action Show => services.Show;
    public void Log(string message) => services.Log(message);
    public void Notify(string message) => services.Notify(message);
    public JsonObject ReadSettings() => services.ReadSettings();
    public void SaveSettings(JsonObject settings) => services.SaveSettings(settings);
    public bool TryRegisterHotkey(Keys chord, Action action) => services.TryRegisterHotkey(chord, action);
    public void Dispose() { /* PluginHost owns and disposes PluginServices. */ }
}

internal sealed class LooseDllHostBridge : IPluginHostBridge
{
    private const string PluginId = "poe.stacked-deck-tracker";
    private readonly object _gate = new();
    private readonly ShortcutWindow _shortcuts = new();

    public string DataDirectory { get; }
    public Action Show { get; }

    public LooseDllHostBridge(Action show, string dataRoot)
    {
        Show = show;
        DataDirectory = Path.Combine(dataRoot, "plugin-data", PluginId);
        Directory.CreateDirectory(DataDirectory);
    }

    public void Log(string message)
    {
        lock (_gate)
        {
            File.AppendAllText(
                Path.Combine(DataDirectory, "plugin.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
    }

    public void Notify(string message) => Log($"[Notice] {message}");

    public JsonObject ReadSettings()
    {
        var path = Path.Combine(DataDirectory, "settings.json");
        lock (_gate)
        {
            return File.Exists(path)
                ? JsonNode.Parse(File.ReadAllBytes(path))?.AsObject() ?? new JsonObject()
                : new JsonObject();
        }
    }

    public void SaveSettings(JsonObject settings)
    {
        var path = Path.Combine(DataDirectory, "settings.json");
        lock (_gate)
        {
            File.WriteAllText(path + ".tmp", settings.ToJsonString());
            File.Move(path + ".tmp", path, overwrite: true);
        }
    }

    public bool TryRegisterHotkey(Keys chord, Action action) => _shortcuts.Register(chord, action);

    public void Dispose() => _shortcuts.Dispose();

    private sealed class ShortcutWindow : NativeWindow, IDisposable
    {
        private readonly Dictionary<int, Action> _actions = new();

        public bool Register(Keys chord, Action action)
        {
            if (Handle == IntPtr.Zero) CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
            var modifiers = (chord.HasFlag(Keys.Control) ? 2 : 0)
                          | (chord.HasFlag(Keys.Shift) ? 4 : 0)
                          | (chord.HasFlag(Keys.Alt) ? 1 : 0);
            var id = _actions.Count + 1;
            if (modifiers == 0 || !RegisterHotKey(Handle, id, modifiers | 0x4000, (int)(chord & Keys.KeyCode)))
                return false;
            _actions.Add(id, action);
            return true;
        }

        protected override void WndProc(ref Message msg)
        {
            if (msg.Msg == 0x312 && _actions.TryGetValue((int)msg.WParam, out var action))
                action();
            else
                base.WndProc(ref msg);
        }

        public void Dispose()
        {
            foreach (var id in _actions.Keys) UnregisterHotKey(Handle, id);
            _actions.Clear();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr window, int id, int modifiers, int key);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);
    }
}
