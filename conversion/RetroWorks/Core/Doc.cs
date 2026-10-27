using System.Text.Json;

namespace RetroWorks.Core;

/// <summary>Base class for all document types (WP, SS, DB). Ported from retroworks.py Doc.</summary>
public abstract class Doc
{
    public const string ExtWp = ".rwp";
    public const string ExtDb = ".rdb";
    public const string ExtSs = ".rss";

    public static readonly Dictionary<string, string> KindName = new()
    {
        ["wp"] = "Word Processor", ["db"] = "Data Base", ["ss"] = "Spreadsheet"
    };
    public static readonly Dictionary<string, string> KindShort = new()
    {
        ["wp"] = "WP", ["db"] = "DB", ["ss"] = "SS"
    };

    protected App App;
    public abstract string Kind { get; }
    public string Name { get; set; }
    public bool Dirty { get; set; }

    /// <summary>
    /// The localStorage key this doc was last persisted under (e.g. "REPORT.rwp"),
    /// or null if it has never been saved to disk. Set on restore so that a later
    /// rename+save can delete the stale entry instead of orphaning it.
    /// </summary>
    public string? SavedKey;

    // Clipboard data (shared via App)
    // WP clipboard
    public List<string> ClipWp = [];
    public List<char> ClipWpDirs = [];
    public List<string> ClipWpSty = [];
    // SS clipboard
    public (string raw, int r, int c, string mode)? ClipSs;
    // DB clipboard
    public List<string>? ClipDb;

    protected Doc(App app, string name)
    {
        App = app;
        Name = Text.SafeName(name);
    }

    public virtual string Title() => Localizer.T("File", Name) + (Dirty ? " *" : "");

    /// <summary>Serialize document data to a JSON-serializable dictionary.</summary>
    public abstract Dictionary<string, object?> ToJson();

    /// <summary>Approximate size in bytes (for the memory game).</summary>
    public virtual int Size() => 0;

    /// <summary>Deserialize from a JSON dictionary.</summary>
    public abstract void FromJson(JsonElement d);

    /// <summary>The full JSON payload for saving/downloading.</summary>
    public string SaveJson()
    {
        var data = new Dictionary<string, object?>
        {
            ["app"] = "RetroWorks",
            ["version"] = 1,
            ["type"] = Kind,
        };
        foreach (var kv in ToJson()) data[kv.Key] = kv.Value;
        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The file extension for this doc type.</summary>
    public string Ext => Kind switch { "wp" => ExtWp, "db" => ExtDb, _ => ExtSs };

    /// <summary>Save: serialize and persist to browser localStorage. Returns true on success.</summary>
    public async Task<bool> DoSaveAsync()
    {
        try
        {
            string json = SaveJson();
            bool ok = await App.SaveLocalFileAsync(Name + Ext, json);
            if (ok)
            {
                // If this doc was previously saved under a different name (rename),
                // delete the stale entry so it can't resurface as a ghost file on
                // next startup — or worse, be silently overwritten by a new file
                // that reuses the old name.
                if (SavedKey is not null && SavedKey != Name + Ext)
                    await App.DeleteLocalFileAsync(SavedKey);
                SavedKey = Name + Ext;

                Dirty = false;
                App.Msg = Localizer.T("SavedNameToDisk", Name, Name + Ext);
            }
            else
            {
                App.Msg = Localizer.S("CouldNotSaveDisk");
            }
            return ok;
        }
        catch (Exception e)
        {
            App.Msg = Localizer.T("CouldNotSave", e.Message);
            return false;
        }
    }

    /// <summary>Download the current document content as a file from the browser.</summary>
    public async Task DoDownloadAsync()
    {
        try
        {
            string json = SaveJson();
            await App.DownloadFile(Name + Ext, json);
            App.Msg = Localizer.T("DownloadedName", Name, Name + Ext);
        }
        catch (Exception e)
        {
            App.Msg = Localizer.T("CouldNotSave", e.Message);
        }
    }

    /// <summary>Load a document from JSON text. Returns the doc or null on failure.</summary>
    public static Doc? LoadFromJson(App app, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string kind = root.GetProperty("type").GetString() ?? "";
        string name = "Untitled";
        Doc? d = kind switch
        {
            "wp" => new WPDoc(app, name),
            "db" => new DBDoc(app, name),
            "ss" => new SSDoc(app, name),
            _ => null
        };
        if (d is null) return null;
        d.FromJson(root);
        return d;
    }

    /// <summary>
    /// The main document loop. Ported from Doc.run(). Runs until Escape (returns "MENU")
    /// or Alt-Q switches to another doc (returns that doc).
    /// </summary>
    public async Task<object?> RunAsync()
    {
        while (true)
        {
            Draw();
            string k = await App.KeyAsync();
            if (k == Key.Escape) return "MENU";
            if (k == Key.Alt("Q"))
            {
                var d = await App.QuickChangeAsync();
                if (d is not null && !ReferenceEquals(d, this)) return d;
                continue;
            }
            if (k == Key.Alt("S"))
            {
                await DoSaveAsync();
                continue;
            }
            if (k == "AS-S")   // Alt+Shift+S → download the file from the browser
            {
                await DoDownloadAsync();
                continue;
            }
            if (k == Key.Alt("K") || k == Key.F1)
            {
                string kindName = KindName.TryGetValue(Kind, out var kn) ? kn : Kind;
                await App.ShowTextAsync(Localizer.T("KindHelp", kindName), HelpText().Split('\n'));
                continue;
            }
            await HandleAsync(k);
        }
    }

    /// <summary>Draw the document to the screen buffer.</summary>
    public abstract void Draw();

    /// <summary>Handle a key token. Override in subclasses.</summary>
    protected virtual Task HandleAsync(string k) => Task.CompletedTask;

    /// <summary>Help text for this doc type.</summary>
    protected virtual string HelpText() => Localizer.Help("About");

    /// <summary>Prompt for an export file name (used by WP print, SS/DB CSV export).</summary>
    public async Task<string?> ExportPathAsync(string ext, string labelKey)
    {
        string defaultName = Name + ext;
        return await App.AskAsync(labelKey, defaultName);
    }

    /// <summary>Trigger a browser download of text content with the given file name.</summary>
    protected Task DownloadText(string fileName, string content) => App.DownloadFile(fileName, content);
}
