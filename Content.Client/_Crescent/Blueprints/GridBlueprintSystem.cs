using System.IO;
using System.IO.Compression;
using System.Text;
using Content.Shared._Crescent.Blueprints;
using Content.Shared.Popups;
using Robust.Shared.Console;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;

namespace Content.Client._Crescent.Blueprints;

public sealed partial class GridBlueprintSystem : EntitySystem
{
    [Dependency] private readonly IConsoleHost _console = default!;
    [Dependency] private readonly IResourceManager _resources = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    public static readonly ResPath BlueprintDirectory = new("/Blueprints");

    private const int MaxNameLength = 48;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<GridBlueprintEvent>(OnBlueprint);
        SubscribeNetworkEvent<GridBlueprintFailedEvent>(OnFailed);
    }

    public void RequestBlueprint()
    {
        RaiseNetworkEvent(new RequestGridBlueprintEvent());
    }

    public void OpenBlueprintFolder()
    {
        _resources.UserData.CreateDir(BlueprintDirectory);
        _resources.UserData.OpenOsWindow(BlueprintDirectory);
    }

    private void OnBlueprint(GridBlueprintEvent ev)
    {
        var path = BlueprintDirectory / $"{SanitizeName(ev.GridName)}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.yml";

        try
        {
            using var compressed = new MemoryStream(ev.Data);
            // the sandbox allows DeflateStream but not GZipStream
            using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);

            _resources.UserData.CreateDir(BlueprintDirectory);
            using var file = _resources.UserData.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
            deflate.CopyTo(file);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to write grid blueprint to {path}: {e}");
            Report(Loc.GetString("grid-blueprint-write-failed"));
            return;
        }

        Report(Loc.GetString("grid-blueprint-saved", ("name", ev.GridName), ("path", path.ToString())));
    }

    private void OnFailed(GridBlueprintFailedEvent ev)
    {
        Report(ev.Reason);
    }

    private void Report(string message)
    {
        _console.WriteLine(null, message);
        _popup.PopupCursor(message);
    }

    private static string SanitizeName(string name)
    {
        var builder = new StringBuilder(Math.Min(name.Length, MaxNameLength));
        foreach (var c in name)
        {
            if (builder.Length >= MaxNameLength)
                break;

            builder.Append(char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_');
        }

        var result = builder.ToString().Trim('_');
        return result.Length == 0 ? "grid" : result;
    }
}

public sealed partial class SaveBlueprintCommand : LocalizedEntityCommands
{
    [Dependency] private readonly GridBlueprintSystem _blueprints = default!;

    public override string Command => "saveblueprint";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        _blueprints.RequestBlueprint();
        shell.WriteLine(Loc.GetString("cmd-saveblueprint-requested"));
    }
}

public sealed partial class BlueprintFolderCommand : LocalizedEntityCommands
{
    [Dependency] private readonly GridBlueprintSystem _blueprints = default!;

    public override string Command => "blueprintfolder";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        _blueprints.OpenBlueprintFolder();
    }
}
