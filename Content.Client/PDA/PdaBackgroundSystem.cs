using System.IO;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;

namespace Content.Client.PDA;

/// <summary>
/// Handles the player's custom PDA background image.
/// The image is stored only in the client's user data folder and is never sent to the server.
/// </summary>
public sealed class PdaBackgroundSystem : EntitySystem
{
    [Dependency] private readonly IResourceManager _resource = default!;
    [Dependency] private readonly IFileDialogManager _fileDialog = default!;

    private static readonly ResPath BackgroundPath = new("/pda_background.png");

    public const int MaxFileSize = 4 * 1024 * 1024;
    public const int MaxDimension = 4096;

    private Texture? _texture;
    private bool _loaded;
    private bool _dialogOpen;

    /// <summary>
    /// Raised when the background is changed or cleared.
    /// </summary>
    public event Action<Texture?>? BackgroundChanged;

    public Texture? Background
    {
        get
        {
            if (!_loaded)
            {
                _loaded = true;
                _texture = LoadSaved();
            }

            return _texture;
        }
    }

    public override void Shutdown()
    {
        base.Shutdown();
        SetTexture(null);
    }

    /// <summary>
    /// Opens a file dialog and sets the chosen image as the PDA background.
    /// </summary>
    public async void PickBackground()
    {
        if (_dialogOpen)
            return;

        _dialogOpen = true;
        byte[]? data;
        try
        {
            var filters = new FileDialogFilters(new FileDialogFilters.Group("png", "jpg", "jpeg"));
            await using var file = await _fileDialog.OpenFile(filters, FileAccess.Read);
            if (file == null)
                return;

            data = await ReadLimited(file);
        }
        catch (Exception e)
        {
            Log.Warning($"Failed to read PDA background: {e}");
            return;
        }
        finally
        {
            _dialogOpen = false;
        }

        if (data == null)
        {
            Log.Warning($"PDA background is larger than {MaxFileSize} bytes, ignoring.");
            return;
        }

        var texture = TryLoadTexture(data);
        if (texture == null)
            return;

        try
        {
            // Re-encoding isn't needed; ImageSharp detects the format from the header on load.
            using var stream = _resource.UserData.Open(BackgroundPath, FileMode.Create);
            stream.Write(data);
        }
        catch (Exception e)
        {
            Log.Warning($"Failed to save PDA background: {e}");
        }

        _loaded = true;
        SetTexture(texture);
    }

    public void ClearBackground()
    {
        if (_resource.UserData.Exists(BackgroundPath))
            _resource.UserData.Delete(BackgroundPath);

        _loaded = true;
        SetTexture(null);
    }

    private Texture? LoadSaved()
    {
        if (!_resource.UserData.Exists(BackgroundPath))
            return null;

        try
        {
            using var stream = _resource.UserData.OpenRead(BackgroundPath);
            if (stream.Length > MaxFileSize)
                return null;

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return TryLoadTexture(memory.ToArray());
        }
        catch (Exception e)
        {
            Log.Warning($"Failed to load saved PDA background: {e}");
            return null;
        }
    }

    private Texture? TryLoadTexture(byte[] data)
    {
        Texture texture;
        try
        {
            using var stream = new MemoryStream(data);
            texture = Texture.LoadFromPNGStream(stream, "PdaBackground");
        }
        catch (Exception e)
        {
            Log.Warning($"Invalid PDA background image: {e.Message}");
            return null;
        }

        if (texture.Width > MaxDimension || texture.Height > MaxDimension)
        {
            Log.Warning($"PDA background is larger than {MaxDimension}x{MaxDimension}, ignoring.");
            (texture as IDisposable)?.Dispose();
            return null;
        }

        return texture;
    }

    private void SetTexture(Texture? texture)
    {
        if (ReferenceEquals(texture, _texture))
            return;

        var old = _texture;
        _texture = texture;
        BackgroundChanged?.Invoke(texture);
        (old as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Reads the whole stream, returning null if it exceeds <see cref="MaxFileSize"/>.
    /// </summary>
    private static async System.Threading.Tasks.Task<byte[]?> ReadLimited(Stream stream)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            if (memory.Length + read > MaxFileSize)
                return null;

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }
}
